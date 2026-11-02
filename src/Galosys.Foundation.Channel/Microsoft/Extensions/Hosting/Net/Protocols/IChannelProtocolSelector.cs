namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>只窥探连接并选择协议；不得消费输入。</summary>
    public interface IChannelProtocolSelector
    {
        /// <summary>返回匹配协议；返回 null 表示无匹配。</summary>
        /// <param name="context">连接视图；选择器只应观察 <see cref="ChannelContext.Reader"/>。</param>
        /// <param name="cancellationToken">连接关闭令牌。</param>
        ValueTask<IChannelProtocol?> SelectAsync(
            ChannelContext context,
            CancellationToken cancellationToken);
    }
}
