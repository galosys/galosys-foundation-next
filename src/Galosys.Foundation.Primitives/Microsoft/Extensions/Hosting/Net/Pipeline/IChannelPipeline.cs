namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>每连接处理管线。</summary>
    public interface IChannelPipeline
    {
        /// <summary>添加中间件，按注册顺序由外向内执行。</summary>
        IChannelPipeline Use(ChannelMiddleware middleware);

        /// <summary>设置终端处理器，只能调用一次。</summary>
        void Run(ChannelHandlerDelegate handler);
    }
}
