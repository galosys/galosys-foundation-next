using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>客户端创建工厂；连接池通过它创建客户端。</summary>
    public interface IChannelClientFactory
    {
        /// <summary>创建新的客户端；每次调用都返回新实例。</summary>
        IChannelClient Create(ChannelClientOptions options);
    }
}
