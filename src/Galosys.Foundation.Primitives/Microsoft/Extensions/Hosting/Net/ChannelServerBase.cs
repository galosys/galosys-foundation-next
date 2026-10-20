using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>监听器基类：绑定、接受连接、执行管线、优雅停机。</summary>
    public abstract class ChannelServerBase : IChannelServer
    {
        private readonly ChannelServerOptions _options;
        private readonly ConcurrentDictionary<string, ChannelContext> _activeConnections = new();
        private readonly ConcurrentDictionary<string, Task> _activeTasks = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private KestrelChannelListener? _listener;
        private ChannelHandlerDelegate? _pipeline;
        private CancellationTokenSource? _stopping;
        private Task? _acceptLoop;
        private int _connectionCount;
        private int _started;
        private int _stopStarted;
        private int _disposed;

        protected ChannelServerBase(EndPoint endPoint)
            : this(new ChannelServerOptions { EndPoint = endPoint })
        {
        }

        protected ChannelServerBase(ChannelServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        internal IServiceScopeFactory? ScopeFactory { get; set; }

        internal ILoggerFactory? LoggerFactory { get; set; }

        public EndPoint? LocalEndPoint { get; private set; }

        /// <summary>默认管线：直接调用 <see cref="ChannelHandleAsync"/>。</summary>
        protected virtual void Configure(IChannelPipeline pipeline) => pipeline.Run(ChannelHandleAsync);

        protected abstract Task ChannelHandleAsync(ChannelContext context);

        public async Task StartAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                return;

            try
            {
                _options.Validate();
                var pipeline = BuildPipeline();
                var listener = await KestrelChannelListener.BindAsync(_options, LoggerFactory, ct).ConfigureAwait(false);

                _pipeline = pipeline;
                _listener = listener;
                LocalEndPoint = listener.LocalEndPoint ?? _options.EndPoint;
                _stopping = new CancellationTokenSource();
                _acceptLoop = AcceptLoopAsync(_stopping.Token);
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                throw;
            }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            await StartAsync(ct).ConfigureAwait(false);

            try
            {
                var acceptLoop = _acceptLoop;
                if (acceptLoop is not null)
                    await acceptLoop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _stopStarted, 1) == 0)
            {
                try
                {
                    await StopCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    _stopped.TrySetResult();
                }
            }

            await _stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _stopping?.Dispose();
        }

        private ChannelHandlerDelegate BuildPipeline()
        {
            var pipeline = new ChannelPipeline();
            Configure(pipeline);
            return pipeline.Build();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var logger = CreateLogger();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var connection = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
                    if (connection is null)
                        break;

                    if (_options.MaxConnections > 0 && Volatile.Read(ref _connectionCount) >= _options.MaxConnections)
                    {
                        logger?.LogWarning("连接数已达到上限 {MaxConnections}，拒绝新连接。", _options.MaxConnections);

                        try
                        {
                            await connection.Transport.Output.CompleteAsync().ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }

                        await connection.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    var id = Guid.NewGuid().ToString("N");
                    var scope = ScopeFactory?.CreateScope() ?? EmptyServiceScope.Instance;
                    var context = new ChannelContext(id, connection, scope.ServiceProvider);

                    _activeConnections[id] = context;

                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _activeTasks[id] = completion.Task;
                    _ = ProcessConnectionAsync(id, context, scope, completion);
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
                logger?.LogError(ex, "接受连接循环异常终止。");
            }
        }

        private async Task ProcessConnectionAsync(
            string id,
            ChannelContext context,
            IServiceScope scope,
            TaskCompletionSource completion)
        {
            Interlocked.Increment(ref _connectionCount);
            Exception? error = null;

            try
            {
                await _pipeline!(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
                CreateLogger()?.LogError(ex, "连接 {ConnectionId} 的处理管线抛出异常。", id);
            }
            finally
            {
                try
                {
                    await context.CompleteAsync(error).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await DisposeScopeAsync(scope).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _connectionCount);
                        _activeConnections.TryRemove(id, out _);
                        _activeTasks.TryRemove(id, out _);
                        completion.TrySetResult();
                    }
                }
            }
        }

        private async Task StopCoreAsync()
        {
            _stopping?.Cancel();

            if (_listener is not null)
            {
                try
                {
                    await _listener.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            var acceptLoop = _acceptLoop;
            if (acceptLoop is not null)
            {
                try
                {
                    await acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            var tasks = _activeTasks.Values.ToArray();
            if (tasks.Length > 0)
            {
                var all = Task.WhenAll(tasks);
                using var timeoutCts = new CancellationTokenSource(_options.ShutdownTimeout);
                var timeout = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

                if (await Task.WhenAny(all, timeout).ConfigureAwait(false) != all)
                {
                    var reason = new TimeoutException(
                        $"在 {_options.ShutdownTimeout} 内仍有连接未完成，强制中止。");

                    foreach (var context in _activeConnections.Values.ToArray())
                        context.Abort(reason);

                    try
                    {
                        await all.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
                else
                {
                    timeoutCts.Cancel();
                    await all.ConfigureAwait(false);
                }
            }

            if (_listener is not null)
            {
                try
                {
                    await _listener.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
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
