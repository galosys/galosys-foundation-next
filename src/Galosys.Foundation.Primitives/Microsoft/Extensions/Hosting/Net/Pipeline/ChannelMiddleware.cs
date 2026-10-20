namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>连接中间件，只允许包装 Transport，不读取业务数据。</summary>
    public delegate Task ChannelMiddleware(ChannelContext context, ChannelHandlerDelegate next);
}
