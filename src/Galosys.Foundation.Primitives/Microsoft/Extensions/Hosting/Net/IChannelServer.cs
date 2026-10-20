using System.Net;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>监听器生命周期契约。</summary>
    public interface IChannelServer : IAsyncDisposable, IDisposable
    {
        /// <summary>绑定成功后的本地端点；未启动时为 null。</summary>
        EndPoint? LocalEndPoint { get; }

        /// <summary>绑定并开始接受连接后返回。</summary>
        Task StartAsync(CancellationToken ct);

        /// <summary>启动后阻塞到停机；取消视为正常停机。</summary>
        Task RunAsync(CancellationToken ct);

        /// <summary>停止接受连接、等待在途连接、超时后强制中止。</summary>
        Task StopAsync(CancellationToken ct);
    }
}
