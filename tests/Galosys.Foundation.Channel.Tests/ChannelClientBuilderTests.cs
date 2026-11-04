using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelClientBuilderTests
{
    [Fact]
    public async Task Build_ProducesConnectableClient()
    {
        await using var server = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .Run(TestHandlers.EchoAsync)
            .Build();

        await server.StartAsync(CancellationToken.None);

        try
        {
            await using var client = ChannelClientBuilder
                .Create(o => o.EndPoint = (IPEndPoint)server.LocalEndPoint!)
                .Build();

            await client.ConnectAsync(CancellationToken.None);

            Assert.Equal(ChannelConnectionState.Connected, client.State);
            Assert.NotNull(client.Connection);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Create_WithInvalidOptions_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ChannelClientBuilder.Create(new ChannelClientOptions()));
    }

    [Fact]
    public async Task AsPool_BuildsPool()
    {
        var pool = ChannelClientBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .AsPool()
            .Build();

        Assert.NotNull(pool);

        await pool.DisposeAsync();
    }

    [Fact]
    public void WithMaxSize_WithNonPositive_Throws()
    {
        var builder = ChannelClientBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .AsPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithMaxSize(0));
    }

    [Fact]
    public void WithRentTimeout_WithNonPositive_Throws()
    {
        var builder = ChannelClientBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .AsPool();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.WithRentTimeout(TimeSpan.Zero));
    }

    [Fact]
    public async Task Build_Twice_ProducesIndependentInstances()
    {
        var builder = ChannelClientBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        var first = builder.Build();
        var second = builder.Build();

        Assert.NotSame(first, second);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }
}
