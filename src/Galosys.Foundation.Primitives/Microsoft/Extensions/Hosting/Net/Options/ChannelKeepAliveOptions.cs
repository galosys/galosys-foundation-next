namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>TCP keep-alive 配置。</summary>
    public sealed class ChannelKeepAliveOptions
    {
        /// <summary>是否启用 keep-alive。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>空闲多久后开始探测。</summary>
        public TimeSpan KeepAliveTime { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>探测间隔。</summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>探测失败重试次数。</summary>
        public int KeepAliveRetryCount { get; set; } = 9;
    }
}
