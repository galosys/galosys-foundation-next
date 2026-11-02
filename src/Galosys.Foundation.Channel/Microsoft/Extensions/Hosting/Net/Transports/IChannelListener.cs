using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>传输监听器契约，仅框架内部使用。</summary>
    internal interface IChannelListener : IAsyncDisposable
    {
        /// <summary>绑定后的本地端点。</summary>
        EndPoint? LocalEndPoint { get; }

        /// <summary>接受一个连接；停止监听时返回 null 或抛出取消异常。</summary>
        ValueTask<IChannelConnection?> AcceptAsync(CancellationToken ct);

        /// <summary>停止接受新连接。</summary>
        ValueTask UnbindAsync(CancellationToken ct);
    }
}
