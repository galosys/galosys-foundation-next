using System.Net;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public async Task StartAsync_Throws_WhenEndPointMissing()
    {
        var server = new TestChannelServer(new ChannelServerOptions(), _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
        Assert.Null(server.LocalEndPoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartAsync_Throws_WhenBacklogInvalid(int backlog)
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                Backlog = backlog,
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_Throws_WhenShutdownTimeoutInvalid()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.Zero,
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_Succeeds_WithBacklogOfOne()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask, options => options.Backlog = 1);

        Assert.NotNull(handle.Server.LocalEndPoint);
    }
}
