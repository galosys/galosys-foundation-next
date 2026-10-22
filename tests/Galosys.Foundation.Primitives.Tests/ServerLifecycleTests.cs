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

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        Assert.True(client.Connected);
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

        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ThrowsAddressAlreadyInUse_WhenPortTaken()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var server = new TestChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, port) },
            _ => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<SocketException>(() => server.StartAsync(CancellationToken.None));
        Assert.Equal(SocketError.AddressAlreadyInUse, exception.SocketErrorCode);
    }

    [Fact]
    public async Task StartAsync_CanRetryAfterFailedBind()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var server = new TestChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, port) },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<SocketException>(() => server.StartAsync(CancellationToken.None));

        blocker.Stop();
        await server.StartAsync(CancellationToken.None);

        Assert.NotNull(server.LocalEndPoint);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptLoop_SurvivesScopeCreationFailure()
    {
        var factory = new ThrowingOnceScopeFactory();
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync, scopeFactory: factory);

        using (var rejected = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.AssertConnectionClosedAsync(rejected.GetStream());
        }

        using var accepted = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = accepted.GetStream();
        await stream.WriteAsync("ok"u8.ToArray());

        Assert.Equal(
            "ok",
            System.Text.Encoding.UTF8.GetString(await TestClient.ReadExactlyAsync(stream, 2)));
        Assert.Equal(1, Volatile.Read(ref factory.CreatedCount));
    }

    [Fact]
    public async Task CustomBoundListenSocket_IsUsed_AndServerStarts()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        var used = false;
        var factory = new Func<EndPoint, Socket>(endPoint =>
        {
            used = true;
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(endPoint);
            return socket;
        });

        await using var handle = await TestServer.StartAsync(
            TestHandlers.EchoAsync,
            options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.IPv6Loopback, 0);
                options.DualMode = true;
                options.CreateBoundListenSocket = factory;
            });

        Assert.True(used);

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();
        await stream.WriteAsync("ipv6"u8.ToArray());

        Assert.Equal(
            "ipv6",
            System.Text.Encoding.UTF8.GetString(await TestClient.ReadExactlyAsync(stream, 4)));
    }

    [Fact]
    public async Task StartAsync_AfterStop_Throws()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        await handle.Server.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handle.Server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentStartAsync_BindsOnce_AndBothCallsComplete()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            _ => Task.CompletedTask);

        await Task.WhenAll(
            server.StartAsync(CancellationToken.None),
            server.StartAsync(CancellationToken.None));

        Assert.NotNull(server.LocalEndPoint);
        await server.DisposeAsync();
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
    public async Task RunAsync_ReturnsWhenStopAsyncIsCalled()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            _ => Task.CompletedTask);

        var runTask = server.RunAsync(CancellationToken.None);
        await TestClient.WaitUntilListeningAsync(server);

        await server.StopAsync(CancellationToken.None);
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
