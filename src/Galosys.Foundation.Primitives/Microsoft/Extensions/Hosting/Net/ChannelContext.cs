using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>连接视图。生命周期由服务器管理，处理器不应释放该上下文。</summary>
    public sealed class ChannelContext : IAsyncDisposable
    {
        private readonly IChannelConnection _connection;
        private readonly CancellationTokenSource _connectionClosed = new();
        private readonly CancellationToken _connectionClosedToken;
        private IDuplexPipe _transport;
        private Exception? _abortReason;
        private int _aborted;
        private int _closedNotified;
        private int _beginClosed;
        private int _connectionDisposed;
        private object? _session;

        internal ChannelContext(string connectionId, IChannelConnection connection, IServiceProvider services)
        {
            ConnectionId = connectionId;
            _connection = connection;
            _transport = connection.Transport;
            Services = services;
            _connectionClosedToken = _connectionClosed.Token;
            _ = ObserveTerminationAsync();
        }

        /// <summary>连接标识，用于日志与追踪关联。</summary>
        public string ConnectionId { get; }

        /// <summary>服务器本地端点。</summary>
        public EndPoint? LocalEndPoint => _connection.LocalEndPoint;

        /// <summary>客户端远端端点。</summary>
        public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

        /// <summary>当前字节传输；默认是底层连接，管线中间件可包装或替换。</summary>
        public IDuplexPipe Transport => _transport;

        /// <summary>当前传输的读取端。</summary>
        public PipeReader Reader => Transport.Input;

        /// <summary>当前传输的写入端。</summary>
        public PipeWriter Writer => Transport.Output;

        /// <summary>连接关闭令牌，恰好触发一次。</summary>
        public CancellationToken ConnectionClosed => _connectionClosedToken;

        /// <summary>每连接的服务提供程序。</summary>
        public IServiceProvider Services { get; }

        /// <summary>连接内杂项状态。非线程安全，只应在处理器执行期间使用。</summary>
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        /// <summary>导致连接中止的首个异常；未中止时为 null。</summary>
        public Exception? AbortReason => Volatile.Read(ref _abortReason);

        internal bool IsAborted => Volatile.Read(ref _aborted) != 0;

        internal bool TrySetSession(object session)
            => Interlocked.CompareExchange(ref _session, session, null) is null;

        internal object? Session => Volatile.Read(ref _session);

        /// <summary>
        /// 替换当前连接的传输。仅允许管线中间件在调用 <c>next</c> 前使用；
        /// 包装后的传输由中间件负责释放，框架只释放底层连接。
        /// </summary>
        public void UseTransport(IDuplexPipe transport)
        {
            ArgumentNullException.ThrowIfNull(transport);
            _transport = transport;
        }

        /// <summary>立即中止连接。</summary>
        public void Abort(Exception? reason = null)
        {
            Interlocked.Exchange(ref _aborted, 1);

            if (reason is not null)
                Interlocked.CompareExchange(ref _abortReason, reason, null);

            NotifyClosed();

            try
            {
                _connection.Abort(_abortReason);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>幂等释放。由框架在连接收尾时调用，处理器不应调用。</summary>
        public async ValueTask DisposeAsync()
        {
            await BeginCloseAsync(IsAborted ? _abortReason : null).ConfigureAwait(false);
            await DisposeConnectionAsync().ConfigureAwait(false);
        }

        internal ValueTask BeginCloseAsync(Exception? error)
        {
            if (Interlocked.Exchange(ref _beginClosed, 1) != 0)
                return ValueTask.CompletedTask;

            if (error is not null || IsAborted)
            {
                Abort(error ?? _abortReason);
            }
            else
            {
                try
                {
                    // 完成当前传输；若中间件替换过传输，由包装层把完成信号传递到底层。
                    _transport.Output.Complete();
                }
                catch (Exception)
                {
                }
            }

            NotifyClosed();
            return ValueTask.CompletedTask;
        }

        internal async ValueTask DisposeConnectionAsync()
        {
            if (Interlocked.Exchange(ref _connectionDisposed, 1) != 0)
                return;

            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            if (_connection.Termination.IsCompletedSuccessfully)
            {
                var terminationReason = _connection.Termination.Result;
                if (terminationReason is not null)
                    Interlocked.CompareExchange(ref _abortReason, terminationReason, null);
            }

            _connectionClosed.Dispose();
        }

        private async Task ObserveTerminationAsync()
        {
            try
            {
                var error = await _connection.Termination.ConfigureAwait(false);

                if (error is not null)
                {
                    Interlocked.CompareExchange(ref _abortReason, error, null);
                    Abort(error);
                }
                else
                {
                    NotifyClosed();
                }
            }
            catch (Exception)
            {
                // 终止观察不能影响连接处理；连接收尾仍由服务器负责。
            }
        }

        private void NotifyClosed()
        {
            if (Interlocked.Exchange(ref _closedNotified, 1) != 0)
                return;

            try
            {
                _connectionClosed.Cancel();
            }
            catch (AggregateException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
