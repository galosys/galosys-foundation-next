using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ServerLifecycleTests
{
    [Fact]
    public async Task StartAsync_ReturnsAfterBinding_AndExposesLocalEndPoint()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        Assert.NotNull(handle.Server.LocalEndPoint);
        Assert.IsType<IPEndPoint>(handle.Server.LocalEndPoint);
        Assert.NotEqual(0, handle.EndPoint.Port);
    }

    [Fact]
    public async Task StartAsync_ThrowsSocketException_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var server = new TestChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, port) },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<SocketException>(() => server.StartAsync(CancellationToken.None));
        Assert.Null(server.LocalEndPoint);
    }

    [Fact]
    public async Task RunAsync_BlocksUntilCancelled_AndStopsServer()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(1),
            },
            TestHandlers.EchoAsync);

        using var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token);
        await TestClient.WaitUntilListeningAsync(server);

        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);
            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        cts.Cancel();
        await runTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(runTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        await handle.Server.StartAsync(CancellationToken.None);
        Assert.NotNull(handle.Server.LocalEndPoint);
    }
}
