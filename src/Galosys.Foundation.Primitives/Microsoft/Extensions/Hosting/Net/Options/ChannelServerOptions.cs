using System.Net;
using System.Net.Sockets;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>服务端监听器配置。</summary>
    public sealed class ChannelServerOptions
    {
        /// <summary>监听端点。必须设置。</summary>
        public EndPoint EndPoint { get; set; } = default!;

        /// <summary>监听 backlog，默认 512。</summary>
        public int Backlog { get; set; } = 512;

        /// <summary>最大并发连接数，0 表示不限制。</summary>
        public int MaxConnections { get; set; }

        /// <summary>是否禁用 Nagle 算法。</summary>
        public bool NoDelay { get; set; } = true;

        /// <summary>TCP keep-alive 配置。</summary>
        public ChannelKeepAliveOptions? KeepAlive { get; set; }

        /// <summary>SO_LINGER 配置。</summary>
        public LingerOption? LingerState { get; set; }

        /// <summary>是否启用 SO_REUSEADDR。</summary>
        public bool ReuseAddress { get; set; }

        /// <summary>IPv6 监听时是否启用双栈。</summary>
        public bool DualMode { get; set; }

        /// <summary>优雅停机等待在途连接的最长时间。</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>读缓冲上限，null 表示由内核决定。</summary>
        public long? MaxReadBufferSize { get; set; }

        /// <summary>写缓冲上限，null 表示由内核决定。</summary>
        public long? MaxWriteBufferSize { get; set; }

        /// <summary>IO 队列数量，0 表示由内核决定。</summary>
        public int IOQueueCount { get; set; }

        /// <summary>自定义创建并绑定监听 socket 的工厂。</summary>
        public Func<EndPoint, Socket>? CreateBoundListenSocket { get; set; }

        internal void Validate()
        {
            if (EndPoint is null)
                throw new ArgumentException("必须提供监听端点 EndPoint。", nameof(EndPoint));

            if (Backlog <= 0)
                throw new ArgumentException("Backlog 必须大于 0。", nameof(Backlog));

            if (MaxConnections < 0)
                throw new ArgumentException("MaxConnections 不能为负数。", nameof(MaxConnections));

            if (ShutdownTimeout <= TimeSpan.Zero)
                throw new ArgumentException("ShutdownTimeout 必须大于 0。", nameof(ShutdownTimeout));

            if (MaxReadBufferSize is <= 0)
                throw new ArgumentException("MaxReadBufferSize 必须大于 0。", nameof(MaxReadBufferSize));

            if (MaxWriteBufferSize is <= 0)
                throw new ArgumentException("MaxWriteBufferSize 必须大于 0。", nameof(MaxWriteBufferSize));

            if (IOQueueCount < 0)
                throw new ArgumentException("IOQueueCount 不能为负数。", nameof(IOQueueCount));
        }
    }
}
