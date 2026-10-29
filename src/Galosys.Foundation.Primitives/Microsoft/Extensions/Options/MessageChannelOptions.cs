namespace Microsoft.Extensions.Options
{
    /// <summary>消息会话配置。</summary>
    public sealed class MessageChannelOptions
    {
        /// <summary>单条消息 payload 的硬上限，默认 1 MiB。</summary>
        public int MaxMessageSize { get; set; } = 1024 * 1024;
    }
}
