using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

internal sealed class EchoHostedServer : ChannelServerBase
{
    public EchoHostedServer(ChannelServerOptions options)
        : base(options)
    {
    }

    protected override Task ChannelHandleAsync(ChannelContext context) => TestHandlers.EchoAsync(context);
}

internal sealed class HoldHostedServer : ChannelServerBase
{
    public static CancellationTokenSource Release { get; set; } = new();

    public HoldHostedServer(ChannelServerOptions options)
        : base(options)
    {
    }

    protected override Task ChannelHandleAsync(ChannelContext context)
        => TestHandlers.HoldAsync(context, Release.Token);
}

public sealed class HostingTests
{
    [Fact]
    public void AddChannelServer_RegistersSameServerInstance()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChannelServer<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        using var host = builder.Build();

        var server = host.Services.GetRequiredService<EchoHostedServer>();
        var contract = host.Services.GetRequiredService<IChannelServer>();

        Assert.Same(server, contract);
    }

    [Fact]
    public async Task Host_StartAsync_BindsServer_AndEchoes()
    {
        using var host = BuildHost<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();

        var server = host.Services.GetRequiredService<EchoHostedServer>();
        Assert.NotNull(server.LocalEndPoint);

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
        var stream = client.GetStream();
        await stream.WriteAsync("ping"u8.ToArray());
        var echoed = await TestClient.ReadExactlyAsync(stream, 4);

        Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));

        client.Dispose();
        await host.StopAsync();
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        using var host = BuildHost<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, port));

        await Assert.ThrowsAsync<SocketException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenEndPointMissing()
    {
        using var host = BuildHost<EchoHostedServer>(_ => { });

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StopAsync_WaitsForInflightConnection()
    {
        HoldHostedServer.Release = new CancellationTokenSource();

        using var host = BuildHost<HoldHostedServer>(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync();
        var server = host.Services.GetRequiredService<HoldHostedServer>();

        using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);

        var stopTask = host.StopAsync();
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        HoldHostedServer.Release.Cancel();
        await stopTask.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Host_Dispose_StopsServer()
    {
        var host = BuildHost<EchoHostedServer>(options =>
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await host.StartAsync();
        var server = host.Services.GetRequiredService<EchoHostedServer>();
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        host.Dispose();

        Assert.Null(server.LocalEndPoint);
        Assert.False(await TestClient.TryConnectAsync(endPoint));
    }

    private static IHost BuildHost<TChannelServer>(Action<ChannelServerOptions> configure)
        where TChannelServer : ChannelServerBase
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChannelServer<TChannelServer>(configure);
        return builder.Build();
    }
}
