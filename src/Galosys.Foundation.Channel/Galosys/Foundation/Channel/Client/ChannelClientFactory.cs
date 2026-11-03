using System;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>默认客户端工厂。</summary>
    public sealed class ChannelClientFactory : IChannelClientFactory
    {
        /// <inheritdoc />
        public IChannelClient Create(ChannelClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return new ChannelClient(options);
        }
    }
}
