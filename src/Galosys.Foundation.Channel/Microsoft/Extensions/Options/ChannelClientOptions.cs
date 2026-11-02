using System;
using System.Net;

namespace Microsoft.Extensions.Options
{
    /// <summary>客户端连接配置。</summary>
    public sealed class ChannelClientOptions
    {
        /// <summary>目标端点。必须设置。</summary>
        public EndPoint EndPoint { get; set; } = default!;

        /// <summary>单次 TCP 连接尝试的超时，默认 10 秒；重连退避不计入。</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>TCP keep-alive 配置；null 表示使用系统默认。</summary>
        public ChannelKeepAliveOptions? KeepAlive { get; set; }

        /// <summary>客户端 TLS 配置；null 表示明文连接。TLS 握手尚未接入。</summary>
        public ChannelTlsOptions? Tls { get; set; }

        /// <summary>重连策略；null 表示失败后不重试。</summary>
        public ChannelReconnectPolicy? Reconnect { get; set; }

        /// <summary>时间源；重连与超时测试可注入假时钟。</summary>
        public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        internal void Validate()
        {
            if (EndPoint is null)
                throw new ArgumentException("必须提供目标端点 EndPoint。", nameof(EndPoint));

            if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromDays(1))
                throw new ArgumentException("ConnectTimeout 必须大于 0 且不超过 1 天。", nameof(ConnectTimeout));

            if (TimeProvider is null)
                throw new ArgumentException("TimeProvider 不能为空。", nameof(TimeProvider));

            if (KeepAlive is { Enabled: true } keepAlive)
            {
                if (keepAlive.KeepAliveTime < TimeSpan.FromSeconds(1))
                    throw new ArgumentException("KeepAliveTime 必须至少为 1 秒。", nameof(KeepAlive));

                if (keepAlive.KeepAliveInterval < TimeSpan.FromSeconds(1))
                    throw new ArgumentException("KeepAliveInterval 必须至少为 1 秒。", nameof(KeepAlive));

                if (keepAlive.KeepAliveRetryCount < 0)
                    throw new ArgumentException("KeepAliveRetryCount 不能为负数。", nameof(KeepAlive));
            }

            Tls?.ValidateClient();
            Reconnect?.Validate();
        }

        internal ChannelClientOptions Clone()
        {
            var clone = (ChannelClientOptions)MemberwiseClone();

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

            if (Reconnect is not null)
                clone.Reconnect = Reconnect.Clone();

            // ChannelTlsOptions 含证书与回调，不深拷贝；调用方不得在连接期间修改同一实例。
            return clone;
        }
    }
}
