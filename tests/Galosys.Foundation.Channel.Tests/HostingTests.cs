using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task Host_StartAsync_BindsServer_AndEchoes()
    {
        await using var server = BuildServer(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await server.StartAsync(CancellationToken.None);

        Assert.NotNull(server.LocalEndPoint);

        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);

            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await using var server = BuildServer(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, port));

        await Assert.ThrowsAsync<SocketException>(
            () => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void Host_StartAsync_Throws_WhenEndPointMissing()
    {
        // 构建器在创建时即校验配置，缺少监听端点会立刻失败。
        Assert.Throws<ArgumentException>(() => BuildServer(TestHandlers.EchoAsync, _ => { }));
    }

    [Fact]
    public async Task Host_StopAsync_WaitsForInflightConnection()
    {
        using var release = new CancellationTokenSource();

        await using var server = BuildServer(
            context => TestHandlers.HoldAsync(context, release.Token),
            options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            });

        await server.StartAsync(CancellationToken.None);

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);

        var stopTask = server.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Host_StopAsync_AbortsConnections_WhenShutdownTimeoutExceeded()
    {
        using var never = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = BuildServer(
            async context =>
            {
                accepted.TrySetResult();
                context.ConnectionClosed.Register(() => closed.TrySetResult());
                await TestHandlers.HoldAsync(context, never.Token);
            },
            options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.ShutdownTimeout = TimeSpan.FromMilliseconds(200);
            });

        await server.StartAsync(CancellationToken.None);

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);

        await server.StopAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);

        await TestClient.AssertConnectionClosedAsync(client.GetStream());
        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
    }

    [Fact]
    public async Task Host_Dispose_StopsServer()
    {
        var server = BuildServer(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        await server.DisposeAsync();

        Assert.Null(server.LocalEndPoint);
        Assert.False(await TestClient.TryConnectAsync(endPoint));
    }

    private static ChannelServer BuildServer(
        Func<ChannelContext, Task> handler,
        Action<ChannelServerOptions> configure)
    {
        var options = new ChannelServerOptions();
        configure(options);

        return ChannelServerBuilder
            .Create(options)
            .Run(context => handler(context))
            .Build();
    }
}
