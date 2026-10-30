using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 socket 的单连接客户端。</summary>
    public sealed class ChannelClient : IChannelClient
    {
        private static readonly TimeSpan CloseDrainTimeout = TimeSpan.FromSeconds(5);

        private readonly object _gate = new();
        private readonly ChannelClientOptions _options;
        private readonly CancellationTokenSource _closed = new();

        private ChannelConnectionState _state = ChannelConnectionState.Disconnected;
        private Task? _connectTask;
        private CancellationTokenSource? _connectCts;
        private Task? _shutdownTask;
        private ChannelContext? _connection;
        private SocketChannelConnection? _transportConnection;
        private int _disposed;

        /// <summary>创建客户端；配置在构造时校验并克隆。</summary>
        public ChannelClient(ChannelClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Clone();
            _options.Validate();
        }

        /// <summary>当前状态。</summary>
        public ChannelConnectionState State
        {
            get
            {
                lock (_gate)
                    return _state;
            }
        }

        /// <summary>仅在 <see cref="ChannelConnectionState.Connected"/> 时非 null。</summary>
        public ChannelContext? Connection
        {
            get
            {
                lock (_gate)
                    return _state == ChannelConnectionState.Connected ? _connection : null;
            }
        }

        /// <summary>状态迁移事件；在锁外同步触发。</summary>
        public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

        /// <summary>建立连接；并发调用共享同一次尝试。</summary>
        public async Task ConnectAsync(CancellationToken ct)
        {
            if (_options.Tls is not null)
                throw new NotSupportedException("客户端 TLS 尚未接入；等待 primitives-tls-transport-1/2 完成后再启用。");

            ct.ThrowIfCancellationRequested();

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
                    _connectCts.CancelAfter(_options.ConnectTimeout);
                    _connectTask = ConnectCoreAsync(_connectCts, ct);
                }

                connectTask = _connectTask;
            }

            await connectTask.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>优雅关闭并进入终态。</summary>
        public Task CloseAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                _shutdownTask ??= ShutdownCoreAsync(ct);
                return _shutdownTask;
            }
        }

        /// <summary>幂等释放；与 <see cref="CloseAsync"/> 共用同一条收尾路径。</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task ConnectCoreAsync(CancellationTokenSource cts, CancellationToken callerToken)
        {
            await Task.Yield();

            Socket? socket = null;
            SocketChannelConnection? connection = null;
            ChannelContext? context = null;

            try
            {
                lock (_gate)
                {
                    if (_state == ChannelConnectionState.Closed)
                        throw new OperationCanceledException(_closed.Token);
                }

                SetState(ChannelConnectionState.Connecting);

                socket = CreateSocket(_options.EndPoint);
                ApplySocketOptions(socket, _options);
                await socket.ConnectAsync(_options.EndPoint, cts.Token).ConfigureAwait(false);

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
                }

                connection = null;
                context = null;

                SetState(ChannelConnectionState.Connected);
                WatchCompletion();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var canceledByClose = _closed.IsCancellationRequested;
                var canceledByCaller = callerToken.IsCancellationRequested && !canceledByClose;

                var failure = canceledByClose
                    ? new ChannelConnectionFailure(
                        ChannelConnectionFailureKind.Canceled,
                        new OperationCanceledException(_closed.Token))
                    : canceledByCaller
                        ? new ChannelConnectionFailure(
                            ChannelConnectionFailureKind.Canceled,
                            new OperationCanceledException(callerToken))
                        : new ChannelConnectionFailure(
                            ChannelConnectionFailureKind.TimedOut,
                            new TimeoutException($"连接在 {_options.ConnectTimeout} 内未完成。"));

                await CleanupAttemptAsync(socket, connection, context).ConfigureAwait(false);

                if (!canceledByClose)
                    SetState(ChannelConnectionState.Disconnected, failure);

                throw failure.Exception;
            }
            catch (Exception ex)
            {
                var failure = ChannelConnectionFailure.FromException(ex);
                await CleanupAttemptAsync(socket, connection, context).ConfigureAwait(false);
                SetState(ChannelConnectionState.Disconnected, failure);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connectCts, cts))
                        _connectCts = null;

                    _connectTask = null;
                }

                cts.Dispose();
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
            catch (ObjectDisposedException)
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
                }
            }

            if (context is null)
                return;

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(CloseDrainTimeout);

            try
            {
                await context.DisposeAsync().AsTask().WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                context.Abort(new OperationCanceledException("关闭等待被取消或超时。"));

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

        private void WatchCompletion()
        {
            _ = WatchCompletionAsync();
        }

        private async Task WatchCompletionAsync()
        {
            try
            {
                // 先让出执行权，保证 ConnectAsync 调用方先观察到 Connected。
                await Task.Yield();

                SocketChannelConnection? connection;
                ChannelContext? context;

                lock (_gate)
                {
                    if (_state != ChannelConnectionState.Connected)
                        return;

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

                SetState(
                    ChannelConnectionState.Disconnected,
                    error is null ? null : ChannelConnectionFailure.FromException(error));
            }
            catch (Exception)
            {
                // 观察器异常不得传播到未观察的任务。
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
