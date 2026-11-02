namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把固定字节前缀绑定到一个协议。</summary>
    public sealed class PrefixProtocolRule
    {
        /// <summary>创建前缀规则；构造时复制前缀。</summary>
        /// <param name="prefix">非空字节前缀。</param>
        /// <param name="protocol">匹配时使用的协议。</param>
        public PrefixProtocolRule(ReadOnlyMemory<byte> prefix, IChannelProtocol protocol)
        {
            if (prefix.IsEmpty)
                throw new ArgumentException("前缀不能为空。", nameof(prefix));

            ArgumentNullException.ThrowIfNull(protocol);

            Prefix = prefix.ToArray();
            Protocol = protocol;
        }

        /// <summary>从连接起始位置匹配的前缀。</summary>
        public ReadOnlyMemory<byte> Prefix { get; }

        /// <summary>匹配时使用的协议。</summary>
        public IChannelProtocol Protocol { get; }
    }
}
