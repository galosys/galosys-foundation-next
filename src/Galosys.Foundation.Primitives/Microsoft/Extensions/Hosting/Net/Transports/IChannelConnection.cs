using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>传输连接契约，仅框架内部使用。</summary>
    internal interface IChannelConnection : IAsyncDisposable
    {
        /// <summary>服务器本地端点。</summary>
        EndPoint? LocalEndPoint { get; }

        /// <summary>客户端远端端点。</summary>
        EndPoint? RemoteEndPoint { get; }

        /// <summary>连接的字节传输。</summary>
        IDuplexPipe Transport { get; }

        /// <summary>
        /// 传输终止信号。对端重置、socket 读写故障或 <see cref="Abort"/> 时完成；
        /// 对端正常半关闭（只关闭发送方向）不触发。异常为终止原因，正常主动中止时为 null。
        /// </summary>
        Task<Exception?> Termination { get; }

        /// <summary>立即中止连接；不刷新未发送的数据。</summary>
        void Abort(Exception? reason);
    }
}
