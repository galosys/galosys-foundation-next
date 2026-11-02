using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 socket 的单连接客户端，可选自动重连。</summary>
    /// <remarks>
    /// 本类型只负责建立、暴露、重连和关闭连接；消息收发通过
    /// <see cref="ChannelContext"/> 的字节管道或 P2 session 完成。
    /// 重连产生新的 <see cref="ChannelContext"/> 与 <c>ConnectionId</c>，
    /// 断开期间不缓冲、不重放消息。连接池见 <see cref="ChannelClientPool"/>。
    /// </remarks>
    public sealed class ChannelClient : IChannelClient
    {
        private static readonly TimeSpan CloseDrainTimeout = TimeSpan.FromSeconds(5);

        private readonly object _gate = new();
        private readonly ChannelClientOptions _options;
        private readonly CancellationTokenSource _closed = new();

        private ChannelConnectionState _state = ChannelConnectionState.Disconnected;
        private Task? _connectTask;
        private CancellationTokenSource? _connectCts;
        private ChannelContext? _connection;
        private SocketChannelConnection? _transportConnection;
        private Task? _shutdownTask;
        private int _disposed;

        /// <summary>创建客户端并校验配置。</summary>
        /// <param name="options">客户端配置；构造时会克隆。</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
        /// <exception cref="ArgumentException">配置无效。</exception>
        public ChannelClient(ChannelClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Clone();
            _options.Validate();
        }

        /// <inheritdoc />
        public ChannelConnectionState State
        {
            get
            {
                lock (_gate)
                    return _state;
            }
        }

        /// <inheritdoc />
        public ChannelContext? Connection
        {
            get
            {
                lock (_gate)
                    return _state == ChannelConnectionState.Connected ? _connection : null;
            }
        }

        /// <inheritdoc />
        public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

        /// <inheritdoc />
        public async Task ConnectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (_options.Tls is not null)
            {
                throw new NotSupportedException(
                    "客户端 TLS 尚未接入；等待 primitives-tls-transport-1/2 完成后再启用。");
            }

            Task connectTask;

            lock (_gate)
            {
                ThrowIfDisposedLocked();

                if (_state == ChannelConnectionState.Closed)
                    throw new InvalidOperationException("客户端已关闭，不能再次连接。");

                if (_state == ChannelConnectionState.Connected)
                    return;

                if (_connectTask is null)
                {
                    _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
                    _connectTask = RunConnectOperationAsync(_connectCts, ct);
                }

                connectTask = _connectTask;
            }

            await connectTask.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task CloseAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                _shutdownTask ??= ShutdownCoreAsync(ct);
                return _shutdownTask;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task RunConnectOperationAsync(
            CancellationTokenSource operationCts,
            CancellationToken callerToken)
        {
            await Task.Yield();

            var policy = _options.Reconnect;
            var delay = policy?.InitialDelay ?? TimeSpan.Zero;
            var retries = 0;

            try
            {
                SetState(ChannelConnectionState.Connecting);

                while (true)
                {
                    try
                    {
                        await ConnectOnceAsync(operationCts.Token, callerToken).ConfigureAwait(false);

                        SetState(ChannelConnectionState.Connected);

                        lock (_gate)
                        {
                            if (_state != ChannelConnectionState.Connected)
                                throw new OperationCanceledException(_closed.Token);
                        }

                        ClearForegroundOperation(operationCts);
                        WatchCompletion();
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var failure = ChannelConnectionFailure.FromException(ex);

                        bool retry;
                        try
                        {
                            retry = CanRetry(policy, failure);
                        }
                        catch (Exception policyEx)
                        {
                            var policyFailure = ChannelConnectionFailure.FromException(policyEx);
                            SetState(ChannelConnectionState.Disconnected, policyFailure);
                            throw;
                        }

                        if (!retry || (policy!.MaxAttempts != 0 && retries >= policy.MaxAttempts))
                        {
                            SetState(ChannelConnectionState.Disconnected, failure);
                            throw;
                        }

                        SetState(ChannelConnectionState.Reconnecting, failure);
                        await DelayBeforeRetryAsync(delay, operationCts.Token).ConfigureAwait(false);

                        retries++;
                        delay = ChannelReconnectPolicy.NextDelay(delay, policy);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!_closed.IsCancellationRequested)
                    SetState(ChannelConnectionState.Disconnected, CanceledFailure(operationCts.Token));

                throw;
            }
            finally
            {
                ClearForegroundOperation(operationCts);
                operationCts.Dispose();
            }
        }

        private async Task ConnectOnceAsync(
            CancellationToken operationToken,
            CancellationToken callerToken)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            attemptCts.CancelAfter(_options.ConnectTimeout);

            Socket? socket = null;
            SocketChannelConnection? connection = null;
            ChannelContext? context = null;

            try
            {
                socket = CreateSocket(_options.EndPoint);
                ApplySocketOptions(socket, _options);
                await socket.ConnectAsync(_options.EndPoint, attemptCts.Token).ConfigureAwait(false);

                connection = new SocketChannelConnection(
                    socket,
                    new ChannelServerOptions { EndPoint = _options.EndPoint },
                    logger: null);
                socket = null;

                context = new ChannelContext(
                    Guid.NewGuid().ToString("N"),
                    connection,
                    EmptyServiceProvider.Instance);

                lock (_gate)
                {
                    if (_state == ChannelConnectionState.Closed)
                        throw new OperationCanceledException(_closed.Token);

                    _connection = context;
                    _transportConnection = connection;
                    context = null;
                    connection = null;
                }
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                await CleanupAttemptAsync(socket, connection, context).ConfigureAwait(false);

                if (operationToken.IsCancellationRequested
                    || callerToken.IsCancellationRequested
                    || _closed.IsCancellationRequested)
                {
                    throw new OperationCanceledException(operationToken);
                }

                throw new TimeoutException($"连接在 {_options.ConnectTimeout} 内未完成。");
            }
            catch
            {
                await CleanupAttemptAsync(socket, connection, context).ConfigureAwait(false);
                throw;
            }
        }

        private async Task ShutdownCoreAsync(CancellationToken ct)
        {
            await Task.Yield();

            ChannelContext? context;
            CancellationTokenSource? connectCts;
            Task? connectTask;
            ChannelStateChangedEventArgs? args = null;

            lock (_gate)
            {
                if (_state != ChannelConnectionState.Closed)
                {
                    args = new ChannelStateChangedEventArgs(_state, ChannelConnectionState.Closed);
                    _state = ChannelConnectionState.Closed;
                }

                context = _connection;
                connectCts = _connectCts;
                connectTask = _connectTask;
                _connection = null;
                _transportConnection = null;
            }

            RaiseStateChanged(args);

            try
            {
                _closed.Cancel();
            }
            catch (Exception)
            {
            }

            try
            {
                connectCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (connectTask is not null)
            {
                try
                {
                    await connectTask.WaitAsync(CloseDrainTimeout).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 连接尝试的失败已经通过状态事件发布；关闭路径只负责收尾。
                }
            }

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(CloseDrainTimeout);

            try
            {
                if (context is not null)
                    await context.DisposeAsync().AsTask().WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                context?.Abort(new OperationCanceledException("关闭等待被取消或超时。"));

                if (context is not null)
                {
                    try
                    {
                        await context.DisposeAsync().AsTask()
                            .WaitAsync(CloseDrainTimeout).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void WatchCompletion()
        {
            _ = WatchCompletionAsync();
        }

        private async Task WatchCompletionAsync()
        {
            SocketChannelConnection? connection;
            ChannelContext? context;

            lock (_gate)
            {
                connection = _transportConnection;
                context = _connection;
            }

            if (connection is null || context is null)
                return;

            await connection.Completion.ConfigureAwait(false);

            lock (_gate)
            {
                if (_state != ChannelConnectionState.Connected || !ReferenceEquals(_connection, context))
                    return;

                _connection = null;
                _transportConnection = null;
            }

            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            var error = connection.Termination.IsCompletedSuccessfully
                ? connection.Termination.Result
                : null;

            var failure = error is not null
                ? ChannelConnectionFailure.FromException(error)
                : new ChannelConnectionFailure(
                    ChannelConnectionFailureKind.Network,
                    new IOException("连接已由对端关闭。"));

            if (_closed.IsCancellationRequested)
                return;

            bool retry;
            try
            {
                retry = CanRetry(_options.Reconnect, failure);
            }
            catch (Exception policyEx)
            {
                SetState(
                    ChannelConnectionState.Disconnected,
                    ChannelConnectionFailure.FromException(policyEx));
                return;
            }

            if (!retry || !StartBackgroundReconnect(failure))
                SetState(ChannelConnectionState.Disconnected, failure);
        }

        private bool StartBackgroundReconnect(ChannelConnectionFailure failure)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ChannelStateChangedEventArgs? args;

            lock (_gate)
            {
                if (_state == ChannelConnectionState.Closed || _connectTask is not null)
                    return false;

                args = new ChannelStateChangedEventArgs(
                    _state,
                    ChannelConnectionState.Reconnecting,
                    failure);
                _state = ChannelConnectionState.Reconnecting;
                _connectTask = completion.Task;
            }

            RaiseStateChanged(args);
            _ = completion.Task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            _ = RunBackgroundReconnectAsync(failure, completion);
            return true;
        }

        private async Task RunBackgroundReconnectAsync(
            ChannelConnectionFailure lastFailure,
            TaskCompletionSource completion)
        {
            await Task.Yield();

            var policy = _options.Reconnect!;
            var delay = policy.InitialDelay;
            var retries = 0;

            try
            {
                while (!_closed.IsCancellationRequested)
                {
                    await DelayBeforeRetryAsync(delay, _closed.Token).ConfigureAwait(false);
                    retries++;

                    try
                    {
                        await ConnectOnceAsync(_closed.Token, CancellationToken.None).ConfigureAwait(false);

                        SetState(ChannelConnectionState.Connected);

                        lock (_gate)
                        {
                            if (ReferenceEquals(_connectTask, completion.Task))
                                _connectTask = null;
                        }

                        WatchCompletion();
                        completion.TrySetResult();
                        return;
                    }
                    catch (OperationCanceledException) when (_closed.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ChannelConnectionFailure.FromException(ex);

                        bool retry;
                        try
                        {
                            retry = CanRetry(policy, lastFailure);
                        }
                        catch (Exception policyEx)
                        {
                            SetState(
                                ChannelConnectionState.Disconnected,
                                ChannelConnectionFailure.FromException(policyEx));
                            completion.TrySetException(policyEx);
                            return;
                        }

                        if (!retry || (policy.MaxAttempts != 0 && retries >= policy.MaxAttempts))
                        {
                            SetState(ChannelConnectionState.Disconnected, lastFailure);
                            completion.TrySetException(lastFailure.Exception);
                            return;
                        }

                        delay = ChannelReconnectPolicy.NextDelay(delay, policy);
                    }
                }
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested)
            {
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                SetState(
                    ChannelConnectionState.Disconnected,
                    ChannelConnectionFailure.FromException(ex));
                completion.TrySetException(ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connectTask, completion.Task))
                        _connectTask = null;
                }
            }
        }

        private async Task DelayBeforeRetryAsync(TimeSpan delay, CancellationToken ct)
        {
            var policy = _options.Reconnect!;
            var jittered = ChannelReconnectPolicy.ApplyJitter(
                delay,
                policy.JitterSource(),
                policy.JitterFactor);

            await Task.Delay(jittered, _options.TimeProvider, ct).ConfigureAwait(false);
        }

        private static bool CanRetry(
            ChannelReconnectPolicy? policy,
            ChannelConnectionFailure failure)
        {
            if (policy is null)
                return false;

            return policy.ShouldRetry?.Invoke(failure.Exception) ?? failure.IsRetryable;
        }

        private static ChannelConnectionFailure CanceledFailure(CancellationToken ct)
            => new(ChannelConnectionFailureKind.Canceled, new OperationCanceledException(ct));

        private void ClearForegroundOperation(CancellationTokenSource operationCts)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connectCts, operationCts))
                {
                    _connectCts = null;
                    _connectTask = null;
                }
            }
        }

        private static Socket CreateSocket(EndPoint endPoint)
        {
            var protocol = endPoint is IPEndPoint ? ProtocolType.Tcp : ProtocolType.Unspecified;
            return new Socket(endPoint.AddressFamily, SocketType.Stream, protocol);
        }

        private static void ApplySocketOptions(Socket socket, ChannelClientOptions options)
        {
            if (socket.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return;

            socket.NoDelay = true;

            if (options.KeepAlive is not { Enabled: true } keepAlive)
                return;

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveTime,
                (int)keepAlive.KeepAliveTime.TotalSeconds);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveInterval,
                (int)keepAlive.KeepAliveInterval.TotalSeconds);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveRetryCount,
                keepAlive.KeepAliveRetryCount);
        }

        private static async ValueTask CleanupAttemptAsync(
            Socket? socket,
            SocketChannelConnection? connection,
            ChannelContext? context)
        {
            if (context is not null)
            {
                try
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            else if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            socket?.Dispose();
        }

        private void SetState(
            ChannelConnectionState state,
            ChannelConnectionFailure? failure = null)
        {
            ChannelStateChangedEventArgs? args = null;

            lock (_gate)
            {
                if (_state == state || _state == ChannelConnectionState.Closed)
                    return;

                args = new ChannelStateChangedEventArgs(_state, state, failure);
                _state = state;
            }

            RaiseStateChanged(args);
        }

        private void RaiseStateChanged(ChannelStateChangedEventArgs? args)
        {
            if (args is null)
                return;

            try
            {
                StateChanged?.Invoke(this, args);
            }
            catch (Exception)
            {
                // 订阅方异常不得破坏连接状态机。
            }
        }

        private void ThrowIfDisposedLocked()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ChannelClient));
        }
    }
}
