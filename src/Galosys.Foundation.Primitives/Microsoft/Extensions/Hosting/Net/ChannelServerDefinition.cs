using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>描述一个由宿主注册的服务器。</summary>
    /// <remarks>
    /// 定义只保存名称、options 快照和 pipeline 配置回调，不持有 socket、日志器或作用域；
    /// 真正的 <see cref="ChannelServer"/> 由 <c>AddChannelServerGroup</c> 在容器解析时创建。
    /// </remarks>
    public sealed class ChannelServerDefinition
    {
        /// <summary>创建服务器定义；options 会被快照。</summary>
        /// <param name="name">诊断名称，不要求全局唯一。</param>
        /// <param name="options">该服务器独占的配置；构造时复制。</param>
        /// <param name="configurePipeline">每连接的 pipeline 配置回调，必须调用 <see cref="IChannelPipeline.Run"/>。</param>
        /// <exception cref="ArgumentException">名称为空白。</exception>
        /// <exception cref="ArgumentNullException">options 或 configurePipeline 为 null。</exception>
        public ChannelServerDefinition(
            string name,
            ChannelServerOptions options,
            Action<IChannelPipeline> configurePipeline)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("名称不能为空。", nameof(name));

            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configurePipeline);

            Name = name;
            Options = options.Clone();
            ConfigurePipeline = configurePipeline;
        }

        /// <summary>诊断名称，不要求全局唯一。</summary>
        public string Name { get; }

        /// <summary>定义构造时的 options 快照。</summary>
        public ChannelServerOptions Options { get; }

        /// <summary>每连接的 pipeline 配置回调。</summary>
        public Action<IChannelPipeline> ConfigurePipeline { get; }
    }
}
