using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task Host_StartAsync_BindsServer_AndEchoes()
    {
        using var host = BuildHost(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();

        var server = host.Services.GetRequiredService<ChannelServer>();
        Assert.NotNull(server.LocalEndPoint);

        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);

            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        await host.StopAsync();
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        using var host = BuildHost(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, port));

        await Assert.ThrowsAsync<SocketException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenEndPointMissing()
    {
        using var host = BuildHost(TestHandlers.EchoAsync, _ => { });

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StopAsync_WaitsForInflightConnection()
    {
        using var release = new CancellationTokenSource();

        using var host = BuildHost(
            context => TestHandlers.HoldAsync(context, release.Token),
            options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.ShutdownTimeout = TimeSpan.FromSeconds(5);
            });

        await host.StartAsync();
        var server = host.Services.GetRequiredService<ChannelServer>();

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);

        var stopTask = host.StopAsync();
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

        using var host = BuildHost(
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

        await host.StartAsync();
        var server = host.Services.GetRequiredService<ChannelServer>();
        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);

        await host.StopAsync().WaitAsync(TestWaits.DefaultTimeout);

        await TestClient.AssertConnectionClosedAsync(client.GetStream());
        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
    }

    [Fact]
    public async Task Host_Dispose_StopsServer()
    {
        var host = BuildHost(TestHandlers.EchoAsync, options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();
        var server = host.Services.GetRequiredService<ChannelServer>();
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        host.Dispose();

        Assert.Null(server.LocalEndPoint);
        Assert.False(await TestClient.TryConnectAsync(endPoint));
    }

    private static IHost BuildHost(
        Func<ChannelContext, Task> handler,
        Action<ChannelServerOptions> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(sp =>
        {
            var options = new ChannelServerOptions();
            configure(options);

            return new ChannelServer(
                options,
                pipeline => pipeline.Run(context => handler(context)),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IServiceScopeFactory>());
        });
        builder.Services.AddHostedService<TestChannelServerRunner>();
        return builder.Build();
    }
}
