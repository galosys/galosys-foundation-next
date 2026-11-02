using System;

namespace Microsoft.Extensions.Options
{
    /// <summary>连接池配置。</summary>
    public sealed class ChannelClientPoolOptions
    {
        /// <summary>池内客户端的连接配置；构造池时会克隆。</summary>
        public ChannelClientOptions ClientOptions { get; set; } = new();

        /// <summary>已创建 + 正在创建 + 已租出的客户端总数上限，默认 8。</summary>
        public int MaxSize { get; set; } = 8;

        /// <summary>池满时等待归还的最长时间，默认 30 秒。</summary>
        public TimeSpan RentTimeout { get; set; } = TimeSpan.FromSeconds(30);

        internal void Validate()
        {
            if (ClientOptions is null)
                throw new ArgumentException("必须提供 ClientOptions。", nameof(ClientOptions));

            ClientOptions.Validate();

            if (MaxSize <= 0)
                throw new ArgumentException("MaxSize 必须大于 0。", nameof(MaxSize));

            if (RentTimeout <= TimeSpan.Zero || RentTimeout > TimeSpan.FromDays(1))
                throw new ArgumentException("RentTimeout 必须大于 0 且不超过 1 天。", nameof(RentTimeout));
        }

        internal ChannelClientPoolOptions Clone()
        {
            return new ChannelClientPoolOptions
            {
                ClientOptions = ClientOptions.Clone(),
                MaxSize = MaxSize,
                RentTimeout = RentTimeout,
            };
        }
    }
}
