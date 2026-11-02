using System;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
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
