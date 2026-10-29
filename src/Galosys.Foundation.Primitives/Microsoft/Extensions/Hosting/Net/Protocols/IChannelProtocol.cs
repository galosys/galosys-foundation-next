namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>同一端口上可被选择的一个协议。</summary>
    public interface IChannelProtocol
    {
        /// <summary>诊断名称，不参与匹配。</summary>
        string Name { get; }

        /// <summary>处理一个已经选择了本协议的连接。</summary>
        /// <param name="context">连接视图；协议不应释放该上下文。</param>
        /// <param name="cancellationToken">连接关闭令牌。</param>
        ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken);
    }
}
