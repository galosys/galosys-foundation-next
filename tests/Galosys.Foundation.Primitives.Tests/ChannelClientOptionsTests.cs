using System.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelClientOptionsTests
{
    [Fact]
    public void Defaults_UseTenSecondTimeoutAndSystemTime()
    {
        var options = new ChannelClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
        };

        Assert.Equal(TimeSpan.FromSeconds(10), options.ConnectTimeout);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Null(options.KeepAlive);
        Assert.Null(options.Tls);
    }

    [Fact]
    public void Validate_Throws_WhenEndPointMissing()
    {
        var options = new ChannelClientOptions();

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenConnectTimeoutInvalid()
    {
        var options = new ChannelClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
            ConnectTimeout = TimeSpan.Zero,
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
