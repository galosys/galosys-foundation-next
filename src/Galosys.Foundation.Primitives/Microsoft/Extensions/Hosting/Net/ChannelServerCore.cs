using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>服务器生命周期内核：绑定、接受连接、执行管线、优雅停机。</summary>
    internal sealed class ChannelServerCore
    {
        private static readonly TimeSpan AcceptLoopStopTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(1);

        private readonly ChannelServerOptions _options;
        private readonly Action<IChannelPipeline> _configurePipeline;
        private readonly ConcurrentDictionary<string, ChannelContext> _activeConnections = new();
        private readonly ConcurrentDictionary<string, Task> _activeTasks = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private IChannelListener? _listener;
        private ChannelHandlerDelegate? _pipeline;
        private CancellationTokenSource? _stopping;
        private Task? _acceptLoop;
        private Exception? _acceptLoopError;
        private int _connectionCount;
        private int _stopStarted;
        private int _disposed;

        public ChannelServerCore(
            ChannelServerOptions options,
            Action<IChannelPipeline> configurePipeline,
            ILoggerFactory? loggerFactory,
            IServiceScopeFactory? scopeFactory)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configurePipeline);

            _options = options.Clone();
            _configurePipeline = configurePipeline;
            LoggerFactory = loggerFactory;
            ScopeFactory = scopeFactory;
        }

        internal IServiceScopeFactory? ScopeFactory { get; }

        internal ILoggerFactory? LoggerFactory { get; }

        public EndPoint? LocalEndPoint { get; private set; }

        /// <summary>绑定端点并启动接受循环；绑定成功后返回。</summary>
        public async Task StartAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _stopStarted) != 0)
                throw new InvalidOperationException("服务器已停止，不能重新启动。");

            if (_acceptLoop is not null)
            {
                ThrowIfAcceptLoopTerminated();
                return;
            }

            await _startLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                if (Volatile.Read(ref _stopStarted) != 0)
                    throw new InvalidOperationException("服务器已停止，不能重新启动。");

                if (_acceptLoop is not null)
                {
                    ThrowIfAcceptLoopTerminated();
                    return;
                }

                _options.Validate();
                var pipeline = BuildPipeline();
                var listener = await SocketChannelListener.BindAsync(_options, LoggerFactory, ct).ConfigureAwait(false);

                _pipeline = pipeline;
                _listener = listener;
                LocalEndPoint = listener.LocalEndPoint ?? _options.EndPoint;
                _stopping = new CancellationTokenSource();
                _acceptLoop = AcceptLoopAsync(listener, _stopping.Token);
            }
            finally
            {
                _startLock.Release();
            }
        }

        /// <summary>启动并阻塞到停机；取消视为正常停机。</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                await StartAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            try
            {
                var acceptLoop = _acceptLoop;
                if (acceptLoop is not null)
                    await acceptLoop.WaitAsync(ct).ConfigureAwait(false);

                if (_acceptLoopError is not null)
                    throw new InvalidOperationException("接受连接循环异常终止。", _acceptLoopError);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>停止接受连接、等待在途连接，超时后强制中止；重复调用幂等。</summary>
        /// <remarks><paramref name="ct"/> 只取消对最终停机完成的等待，不取消停机流程本身。</remarks>
        public async Task StopAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _stopStarted, 1) == 0)
            {
                await _startLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

                try
                {
                    await StopCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    _startLock.Release();
                    _stopped.TrySetResult();
                }
            }

            await _stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>同步释放；等价于阻塞等待 <see cref="DisposeAsync"/>。</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        /// <summary>异步停止并释放监听器；重复调用幂等。</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await _disposeCompleted.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _disposeCompleted.TrySetException(ex);
                throw;
            }
            finally
            {
                _stopping?.Dispose();
            }

            _disposeCompleted.TrySetResult();
        }

        private ChannelHandlerDelegate BuildPipeline()
        {
            var pipeline = new ChannelPipeline();
            _configurePipeline(pipeline);
            return pipeline.Build();
        }

        private async Task AcceptLoopAsync(IChannelListener listener, CancellationToken ct)
        {
            var logger = CreateLogger();
            var consecutiveAcceptFailures = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    IChannelConnection? connection = null;

                    try
                    {
                        connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
                        consecutiveAcceptFailures = 0;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (SocketException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        consecutiveAcceptFailures++;

                        if (consecutiveAcceptFailures >= 10)
                        {
                            _acceptLoopError = ex;
                            logger?.LogError(ex, "接受连接连续失败，停止接受循环。");
                            break;
                        }

                        logger?.LogWarning(ex, "接受连接失败，继续监听。");

                        try
                        {
                            await Task.Delay(10, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        continue;
                    }

                    if (connection is null)
                        break;

                    if (_options.MaxConnections > 0 && Volatile.Read(ref _connectionCount) >= _options.MaxConnections)
                    {
                        logger?.LogDebug("连接数已达到上限 {MaxConnections}，拒绝新连接。", _options.MaxConnections);

                        connection.Abort(new IOException(
                            $"连接数已达到上限 {_options.MaxConnections}。"));

                        try
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "释放被拒绝的连接时发生异常。");
                        }

                        continue;
                    }

                    IServiceScope? scope = null;

                    try
                    {
                        scope = ScopeFactory?.CreateScope() ?? EmptyServiceScope.Instance;
                        var id = Guid.NewGuid().ToString("N");
                        var context = new ChannelContext(id, connection, scope.ServiceProvider);

                        _activeConnections[id] = context;

                        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _activeTasks[id] = completion.Task;
                        _ = ProcessConnectionAsync(id, context, scope, completion);

                        scope = null;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "为连接创建处理上下文失败，已拒绝该连接。");

                        try
                        {
                            connection.Abort(new IOException("连接初始化失败。"));
                        }
                        catch (Exception abortEx)
                        {
                            logger?.LogDebug(abortEx, "中止初始化失败的连接时发生异常。");
                        }

                        try
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception disposeEx)
                        {
                            logger?.LogDebug(disposeEx, "释放初始化失败的连接时发生异常。");
                        }

                        if (scope is not null)
                        {
                            try
                            {
                                await DisposeScopeAsync(scope).ConfigureAwait(false);
                            }
                            catch (Exception scopeEx)
                            {
                                logger?.LogError(scopeEx, "释放初始化失败连接的作用域时发生异常。");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _acceptLoopError = ex;
                logger?.LogError(ex, "接受连接循环异常终止。");
            }
        }

        private void ThrowIfAcceptLoopTerminated()
        {
            if (_acceptLoop is { IsCompleted: true })
                throw new InvalidOperationException("接受连接循环已终止，服务器无法继续接受连接。", _acceptLoopError);
        }

        private async Task ProcessConnectionAsync(
            string id,
            ChannelContext context,
            IServiceScope scope,
            TaskCompletionSource completion)
        {
            Interlocked.Increment(ref _connectionCount);

            ILogger? logger = null;
            Exception? error = null;

            try
            {
                logger = CreateLogger();

                try
                {
                    await _pipeline!(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.ConnectionClosed.IsCancellationRequested)
                {
                    // 对端断开或停机导致取消，属于正常收尾。
                }
                catch (Exception ex)
                {
                    error = ex;
                    logger?.LogError(ex, "连接 {ConnectionId} 的处理管线抛出异常。", id);
                }
            }
            finally
            {
                try
                {
                    await context.BeginCloseAsync(error).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "关闭连接 {ConnectionId} 时发生异常。", id);
                }

                try
                {
                    await DisposeScopeAsync(scope).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "释放连接 {ConnectionId} 的作用域时发生异常。", id);
                }

                try
                {
                    await context.DisposeConnectionAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "释放连接 {ConnectionId} 的底层传输时发生异常。", id);
                }

                Interlocked.Decrement(ref _connectionCount);
                _activeConnections.TryRemove(id, out _);
                _activeTasks.TryRemove(id, out _);
                completion.TrySetResult();
            }
        }

        private async Task StopCoreAsync()
        {
            var listener = _listener;
            _listener = null;
            ILogger? logger = null;

            try
            {
                logger = CreateLogger();
                try
                {
                    _stopping?.Cancel();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "取消停机令牌时发生异常。");
                }

                if (listener is not null)
                {
                    try
                    {
                        await listener.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "解绑监听器时发生异常。");
                    }
                }

                var acceptLoop = _acceptLoop;
                if (acceptLoop is not null)
                {
                    try
                    {
                        await acceptLoop.WaitAsync(AcceptLoopStopTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        logger?.LogWarning(
                            ex,
                            "接受连接循环未在 {Timeout} 内退出。",
                            AcceptLoopStopTimeout);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "等待接受循环退出时发生异常。");
                    }
                }

                await WaitForInflightAsync(logger).ConfigureAwait(false);
            }
            finally
            {
                if (listener is not null)
                {
                    try
                    {
                        await listener.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "释放监听器时发生异常。");
                    }
                }

                LocalEndPoint = null;
            }
        }

        private async Task WaitForInflightAsync(ILogger? logger)
        {
            var tasks = _activeTasks.Values.ToArray();
            if (tasks.Length == 0)
                return;

            var all = Task.WhenAll(tasks);
            using var timeoutCts = new CancellationTokenSource(_options.ShutdownTimeout);
            var timeout = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

            if (await Task.WhenAny(all, timeout).ConfigureAwait(false) == all)
            {
                timeoutCts.Cancel();
                await all.ConfigureAwait(false);
                return;
            }

            var reason = new TimeoutException(
                $"在 {_options.ShutdownTimeout} 内仍有连接未完成，强制中止。");

            foreach (var context in _activeConnections.Values.ToArray())
            {
                // 不在停机线程上同步触发 ConnectionClosed 回调：回调可能重入 StopAsync，
                // 而 _stopped 要等 StopCoreAsync 返回，同步执行会永久互等。
                var target = context;
                ThreadPool.UnsafeQueueUserWorkItem(
                    _ =>
                    {
                        try
                        {
                            target.Abort(reason);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "强制中止连接时发生异常。");
                        }
                    },
                    null);
            }

            try
            {
                await all.WaitAsync(AbortGracePeriod).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "强制中止后仍有连接未在宽限期内退出。");
            }
        }

        private static async ValueTask DisposeScopeAsync(IServiceScope scope)
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                return;
            }

            scope.Dispose();
        }

        private ILogger? CreateLogger()
            => LoggerFactory?.CreateLogger("Microsoft.Extensions.Hosting.Net.ChannelServer");
    }
}
