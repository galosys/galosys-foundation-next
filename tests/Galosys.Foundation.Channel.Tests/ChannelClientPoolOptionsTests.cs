using System;
using System.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelClientPoolOptionsTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        var options = new ChannelClientPoolOptions();

        Assert.Equal(8, options.MaxSize);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RentTimeout);
        Assert.NotNull(options.ClientOptions);
    }

    [Fact]
    public void Validate_Throws_WhenClientOptionsNull()
    {
        var options = new ChannelClientPoolOptions
        {
            ClientOptions = null!,
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenClientOptionsInvalid()
    {
        var options = new ChannelClientPoolOptions
        {
            ClientOptions = new ChannelClientOptions(),
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenMaxSizeNotPositive()
    {
        var options = new ChannelClientPoolOptions
        {
            ClientOptions = ValidClientOptions(),
            MaxSize = 0,
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenRentTimeoutInvalid(int milliseconds)
    {
        var options = new ChannelClientPoolOptions
        {
            ClientOptions = ValidClientOptions(),
            RentTimeout = TimeSpan.FromMilliseconds(milliseconds),
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenRentTimeoutTooLong()
    {
        var options = new ChannelClientPoolOptions
        {
            ClientOptions = ValidClientOptions(),
            RentTimeout = TimeSpan.FromDays(2),
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Clone_IsolatesClientOptions()
    {
        var options = new ChannelClientPoolOptions
        {
            ClientOptions = ValidClientOptions(),
            MaxSize = 2,
            RentTimeout = TimeSpan.FromSeconds(5),
        };

        var clone = options.Clone();
        options.ClientOptions.EndPoint = new IPEndPoint(IPAddress.Loopback, 1);
        options.MaxSize = 9;

        Assert.NotSame(options.ClientOptions, clone.ClientOptions);
        Assert.NotEqual(options.ClientOptions.EndPoint, clone.ClientOptions.EndPoint);
        Assert.Equal(2, clone.MaxSize);
        Assert.Equal(TimeSpan.FromSeconds(5), clone.RentTimeout);
    }

    private static ChannelClientOptions ValidClientOptions()
        => new()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
        };
}
