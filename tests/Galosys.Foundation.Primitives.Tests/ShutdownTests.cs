using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ShutdownTests
{
    [Fact]
    public async Task StopAsync_WaitsForInflightConnection()
    {
        using var release = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                accepted.TrySetResult();
                await TestHandlers.HoldAsync(context, release.Token);
            },
            options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);

        var stopTask = handle.Server.StopAsync(CancellationToken.None);

        await TestClient.WaitUntilAsync(
            async () => !await TestClient.TryConnectAsync(handle.EndPoint),
            "停机开始后仍能建立新连接。");

        Assert.False(stopTask.IsCompleted);

        release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAsync_AbortsConnections_WhenShutdownTimeoutExceeded()
    {
        using var never = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                accepted.TrySetResult();
                context.ConnectionClosed.Register(() => closed.TrySetResult());
                await TestHandlers.HoldAsync(context, never.Token);
            },
            options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);
        var stream = client.GetStream();

        var stopTask = handle.Server.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);

        await TestClient.AssertConnectionClosedAsync(stream);
        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
    }

    [Fact]
    public async Task StopAsync_FromConnectionClosedCallback_DoesNotDeadlock()
    {
        using var never = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChannelServerBase? server = null;

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                accepted.TrySetResult();
                context.ConnectionClosed.Register(() =>
                {
                    reentered.TrySetResult();
                    _ = server!.StopAsync(CancellationToken.None);
                });

                await TestHandlers.HoldAsync(context, never.Token);
            },
            options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));

        server = handle.Server;

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);

        await handle.Server.StopAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);
        await reentered.Task.WaitAsync(TestWaits.DefaultTimeout);
    }

    [Fact]
    public async Task StopAsync_StopsAcceptingNewConnections()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);
        var endPoint = handle.EndPoint;

        await handle.Server.StopAsync(CancellationToken.None);

        await TestClient.AssertConnectFailsAsync(endPoint);
    }

    [Fact]
    public async Task StartAsync_Throws_WhenShutdownTimeoutNegative()
    {
        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(-1),
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_AbortsActiveConnections()
    {
        using var never = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new TestChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromMilliseconds(200),
            },
            async context =>
            {
                accepted.TrySetResult();
                context.ConnectionClosed.Register(() => closed.TrySetResult());
                await TestHandlers.HoldAsync(context, never.Token);
            });

        await server.StartAsync(CancellationToken.None);
        using var client = await TestClient.ConnectAsync((System.Net.IPEndPoint)server.LocalEndPoint!);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);

        await server.DisposeAsync();

        await TestClient.AssertConnectionClosedAsync(client.GetStream());
        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallers_AllComplete()
    {
        var handle = await TestServer.StartAsync(_ => Task.CompletedTask);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => handle.Server.DisposeAsync().AsTask()))
            .WaitAsync(TestWaits.DefaultTimeout);

        await TestClient.AssertConnectFailsAsync(handle.EndPoint);
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
