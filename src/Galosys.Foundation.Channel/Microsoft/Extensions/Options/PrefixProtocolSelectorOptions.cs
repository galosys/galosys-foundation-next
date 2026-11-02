namespace Microsoft.Extensions.Options
{
    /// <summary>前缀选择器的扫描上限。</summary>
    public sealed class PrefixProtocolSelectorOptions
    {
        /// <summary>单条前缀和未知协议扫描的最大字节数，默认 64。</summary>
        public int MaxPrefixSize { get; set; } = 64;
    }
}
