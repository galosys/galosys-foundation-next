namespace Microsoft.Extensions.Options
{
    /// <summary>长度前缀分帧配置。</summary>
    public sealed class LengthFieldFramerOptions
    {
        /// <summary>单帧 payload 的硬上限，默认 1 MiB。</summary>
        public int MaxFrameSize { get; set; } = 1024 * 1024;
    }
}
