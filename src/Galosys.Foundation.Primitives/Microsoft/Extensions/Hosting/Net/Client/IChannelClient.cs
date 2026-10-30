using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>单连接客户端契约；重连与连接池在后续 change 叠加。</summary>
    public interface IChannelClient : IAsyncDisposable
    {
        /// <summary>当前状态。</summary>
        ChannelConnectionState State { get; }

        /// <summary>仅在 <see cref="ChannelConnectionState.Connected"/> 时非 null。</summary>
        ChannelContext? Connection { get; }

        /// <summary>状态迁移事件；在锁外同步触发。</summary>
        event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

        /// <summary>建立连接；并发调用共享同一次尝试。</summary>
        Task ConnectAsync(CancellationToken ct);

        /// <summary>优雅关闭并进入终态。</summary>
        Task CloseAsync(CancellationToken ct);
    }
}
