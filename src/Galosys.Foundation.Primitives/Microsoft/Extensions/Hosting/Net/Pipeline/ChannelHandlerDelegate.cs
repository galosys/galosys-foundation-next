namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>连接终端处理器。</summary>
    public delegate Task ChannelHandlerDelegate(ChannelContext context);
}
