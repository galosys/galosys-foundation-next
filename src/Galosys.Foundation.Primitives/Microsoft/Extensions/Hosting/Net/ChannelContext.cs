using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>只读连接视图。构造为 internal，外部不可伪造。</summary>
    public sealed class ChannelContext : IAsyncDisposable
    {
        private readonly ConnectionContext _connection;
        private readonly CancellationTokenSource _connectionClosed = new();
        private readonly CancellationToken _connectionClosedToken;
        private Exception? _abortReason;
        private int _closedNotified;
        private int _completed;

        internal ChannelContext(string connectionId, ConnectionContext connection, IServiceProvider services)
        {
            ConnectionId = connectionId;
            _connection = connection;
            Services = services;
            _connectionClosedToken = _connectionClosed.Token;
        }

        /// <summary>连接标识，用于日志与追踪关联。</summary>
        public string ConnectionId { get; }

        public EndPoint? LocalEndPoint => _connection.LocalEndPoint;

        public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

        /// <summary>原始字节传输。</summary>
        public IDuplexPipe Transport => _connection.Transport;

        public PipeReader Reader => Transport.Input;

        public PipeWriter Writer => Transport.Output;

        /// <summary>连接关闭令牌，恰好触发一次。</summary>
        public CancellationToken ConnectionClosed => _connectionClosedToken;

        /// <summary>每连接的服务提供程序。</summary>
        public IServiceProvider Services { get; }

        /// <summary>连接内杂项状态，不放服务。</summary>
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        internal bool IsAborted => _abortReason is not null;

        /// <summary>立即中止连接。</summary>
        public void Abort(Exception? reason = null)
        {
            if (reason is not null)
                Interlocked.CompareExchange(ref _abortReason, reason, null);

            NotifyClosed();

            try
            {
                _connection.Abort(new ConnectionAbortedException(_abortReason?.Message ?? "连接已中止。", _abortReason!));
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>幂等释放；收尾时完成输出、通知关闭并释放底层连接。</summary>
        public ValueTask DisposeAsync() => CompleteAsync(IsAborted ? _abortReason : null);

        internal async ValueTask CompleteAsync(Exception? error)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            if (error is not null)
                Abort(error);

            try
            {
                Transport.Output.Complete();
            }
            catch (Exception)
            {
            }

            NotifyClosed();

            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _connectionClosed.Dispose();
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
