using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ShutdownTests
{
    [Fact]
    public async Task StopAsync_WaitsForInflightConnection()
    {
        using var release = new CancellationTokenSource();
        await using var handle = await TestServer.StartAsync(
            context => TestHandlers.HoldAsync(context, release.Token),
            options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stopTask = handle.Server.StopAsync(CancellationToken.None);

        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAsync_AbortsConnections_WhenShutdownTimeoutExceeded()
    {
        using var never = new CancellationTokenSource();
        await using var handle = await TestServer.StartAsync(
            context => TestHandlers.HoldAsync(context, never.Token),
            options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var stopTask = handle.Server.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);

        await TestClient.AssertConnectionClosedAsync(stream);
    }

    [Fact]
    public async Task StopAsync_And_DisposeAsync_AreIdempotent()
    {
        var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        await handle.Server.StopAsync(CancellationToken.None);
        await handle.Server.StopAsync(CancellationToken.None);
        await handle.Server.DisposeAsync();
        await handle.Server.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWithoutStart_DoesNotThrow()
    {
        var server = new TestChannelServer(new ChannelServerOptions(), _ => Task.CompletedTask);

        await server.DisposeAsync();
        Assert.Null(server.LocalEndPoint);
    }
}
