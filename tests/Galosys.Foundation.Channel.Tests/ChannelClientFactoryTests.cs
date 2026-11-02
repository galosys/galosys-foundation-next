using System.Net;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelClientFactoryTests
{
    [Fact]
    public void Create_ReturnsNewClientEachTime()
    {
        var factory = new ChannelClientFactory();
        var options = new ChannelClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
        };

        var first = factory.Create(options);
        var second = factory.Create(options);

        Assert.NotSame(first, second);
        Assert.IsAssignableFrom<IChannelClient>(first);
        Assert.IsAssignableFrom<IChannelClient>(second);
    }

    [Fact]
    public void Create_Throws_WhenOptionsNull()
    {
        var factory = new ChannelClientFactory();

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }
}
