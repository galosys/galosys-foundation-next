using System.Net;
using System.Net.Sockets;

namespace Microsoft.Extensions.Options
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

        /// <summary>是否禁用 Nagle 算法（仅 TCP）。</summary>
        public bool NoDelay { get; set; } = true;

        /// <summary>TCP keep-alive 配置（仅 TCP）。</summary>
        public ChannelKeepAliveOptions? KeepAlive { get; set; }

        /// <summary>SO_LINGER 配置，应用于已接受的连接。</summary>
        public LingerOption? LingerState { get; set; }

        /// <summary>是否启用 SO_REUSEADDR。</summary>
        public bool ReuseAddress { get; set; }

        /// <summary>IPv6 监听时是否启用双栈。</summary>
        public bool DualMode { get; set; }

        /// <summary>优雅停机等待在途连接的最长时间。</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>输入管道中未消费数据的上限，默认 1 MiB；超过后暂停接收，形成背压。</summary>
        public long? MaxReadBufferSize { get; set; } = 1024 * 1024;

        /// <summary>输出管道中未发送数据的上限，默认 64 KiB；超过后暂停写入，形成背压。</summary>
        public long? MaxWriteBufferSize { get; set; } = 64 * 1024;

        /// <summary>
        /// 自定义创建并绑定监听 socket 的工厂。工厂负责套接字选项与绑定；
        /// 框架随后调用 <c>Listen</c>，失败时负责释放返回的 socket。
        /// </summary>
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

            if (ShutdownTimeout > TimeSpan.FromDays(1))
                throw new ArgumentException("ShutdownTimeout 不能超过 1 天。", nameof(ShutdownTimeout));

            if (MaxReadBufferSize is null or <= 0)
                throw new ArgumentException("MaxReadBufferSize 必须大于 0。", nameof(MaxReadBufferSize));

            if (MaxWriteBufferSize is null or <= 0)
                throw new ArgumentException("MaxWriteBufferSize 必须大于 0。", nameof(MaxWriteBufferSize));

            if (KeepAlive is { Enabled: true } keepAlive)
            {
                if (keepAlive.KeepAliveTime < TimeSpan.FromSeconds(1))
                    throw new ArgumentException("KeepAliveTime 必须至少为 1 秒。", nameof(KeepAlive));

                if (keepAlive.KeepAliveInterval < TimeSpan.FromSeconds(1))
                    throw new ArgumentException("KeepAliveInterval 必须至少为 1 秒。", nameof(KeepAlive));

                if (keepAlive.KeepAliveRetryCount < 0)
                    throw new ArgumentException("KeepAliveRetryCount 不能为负数。", nameof(KeepAlive));
            }
        }

        /// <summary>复制配置，避免启动后被外部修改影响运行中的服务器。</summary>
        internal ChannelServerOptions Clone()
        {
            var clone = (ChannelServerOptions)MemberwiseClone();

            if (KeepAlive is not null)
            {
                clone.KeepAlive = new ChannelKeepAliveOptions
                {
                    Enabled = KeepAlive.Enabled,
                    KeepAliveTime = KeepAlive.KeepAliveTime,
                    KeepAliveInterval = KeepAlive.KeepAliveInterval,
                    KeepAliveRetryCount = KeepAlive.KeepAliveRetryCount,
                };
            }

            if (LingerState is not null)
                clone.LingerState = new LingerOption(LingerState.Enabled, LingerState.LingerTime);

            return clone;
        }
    }
}
