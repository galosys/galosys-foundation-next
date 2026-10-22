using System.Net;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

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
    public async Task StartAsync_Throws_WhenShutdownTimeoutTooLarge()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromDays(2),
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void Defaults_EnableTransportBackpressure()
    {
        var options = new ChannelServerOptions();

        Assert.Equal(1024L * 1024, options.MaxReadBufferSize);
        Assert.Equal(64L * 1024, options.MaxWriteBufferSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task StartAsync_Throws_WhenReadBufferInvalid(long? size)
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                MaxReadBufferSize = size,
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task StartAsync_Throws_WhenWriteBufferInvalid(long? size)
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                MaxWriteBufferSize = size,
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
