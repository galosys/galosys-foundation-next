using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ServerGroupHostingTests
{
    [Fact]
    public async Task Host_StartAsync_BindsBothDefinitions_AndStopsThem()
    {
        using var host = BuildHost(
            EchoDefinition("first"),
            EchoDefinition("second"));

        await host.StartAsync();

        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        Assert.Equal(2, group.Servers.Count);
        Assert.All(group.Servers, server => Assert.NotNull(server.LocalEndPoint));

        var firstEndPoint = (IPEndPoint)group.Servers[0].LocalEndPoint!;
        using (var client = await TestClient.ConnectAsync(firstEndPoint))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);
            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        await host.StopAsync();
        Assert.All(group.Servers, server => Assert.Null(server.LocalEndPoint));
    }

    [Fact]
    public async Task Host_UsesDefinitionOrder_AndIndependentOptions()
    {
        var firstPort = GetFreePort();
        var secondPort = GetFreePort();

        using var host = BuildHost(
            EchoDefinition("first", firstPort, TimeSpan.FromSeconds(1), maxConnections: 1),
            EchoDefinition("second", secondPort, TimeSpan.FromSeconds(3), maxConnections: 0));

        await host.StartAsync();

        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        Assert.Equal(firstPort, ((IPEndPoint)group.Servers[0].LocalEndPoint!).Port);
        Assert.Equal(secondPort, ((IPEndPoint)group.Servers[1].LocalEndPoint!).Port);

        await host.StopAsync();
    }

    [Fact]
    public async Task Host_WithoutAddChannelServerGroup_DoesNotStartDefinitions()
    {
        var port = GetFreePort();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChannelServer(EchoDefinition("orphan", port));

        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(ChannelServerOptions));

        using var host = builder.Build();
        await host.StartAsync();

        Assert.Null(host.Services.GetService<ChannelServerGroup>());
        Assert.False(await TestClient.TryConnectAsync(new IPEndPoint(IPAddress.Loopback, port)));
        await host.StopAsync();
    }

    [Fact]
    public void Definition_RejectsInvalidArguments_AndSnapshotsOptions()
    {
        var options = new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        };

        var definition = new ChannelServerDefinition("first", options, _ => { });
        options.EndPoint = new IPEndPoint(IPAddress.Loopback, 12345);

        Assert.NotEqual(12345, ((IPEndPoint)definition.Options.EndPoint!).Port);
        Assert.Equal("first", definition.Name);
        Assert.Throws<ArgumentException>(
            () => new ChannelServerDefinition(" ", options, _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => new ChannelServerDefinition("x", null!, _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => new ChannelServerDefinition("x", options, null!));
    }

    [Fact]
    public async Task Host_DisposeAsync_WithoutStop_ReleasesServers()
    {
        var host = BuildHost(EchoDefinition("first"));
        await host.StartAsync();

        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        var endPoint = (IPEndPoint)group.Servers[0].LocalEndPoint!;

        await ((IAsyncDisposable)host).DisposeAsync();

        Assert.Null(group.Servers[0].LocalEndPoint);
        Assert.False(await TestClient.TryConnectAsync(endPoint));
    }

    [Fact]
    public async Task Host_DuplicateAddChannelServerGroup_RegistersOneHostedService()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddChannelServer(EchoDefinition("first"));
        builder.Services.AddChannelServerGroup();
        builder.Services.AddChannelServerGroup();

        Assert.Equal(1, builder.Services.Count(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ChannelServerGroupHostedService)));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var group = host.Services.GetRequiredService<ChannelServerGroup>();
            Assert.Single(group.Servers);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_PropagatesScopeFactory_ToEveryDefinition()
    {
        GroupScopedProbe.Reset();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<GroupScopedProbe>();
        builder.Services.AddChannelServer(ScopedDefinition("first"));
        builder.Services.AddChannelServer(ScopedDefinition("second"));
        builder.Services.AddChannelServerGroup();

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var group = host.Services.GetRequiredService<ChannelServerGroup>();
            using var first = await TestClient.ConnectAsync(
                (IPEndPoint)group.Servers[0].LocalEndPoint!);
            using var second = await TestClient.ConnectAsync(
                (IPEndPoint)group.Servers[1].LocalEndPoint!);

            await TestClient.WaitUntilAsync(
                () => Task.FromResult(
                    GroupScopedProbe.Instances.Count == 2 &&
                    GroupScopedProbe.DisposedCount == 2),
                "两个连接未创建并释放独立的 scoped 实例。");

            Assert.Equal(2, GroupScopedProbe.Instances.Distinct().Count());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_StartupFailure_RollsBackPreviouslyStartedServers()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var first = EchoDefinition("first");
        var second = new ChannelServerDefinition(
            "second",
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, busyPort),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        using var host = BuildHost(first, second);

        await Assert.ThrowsAsync<SocketException>(() => host.StartAsync());

        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        Assert.Null(group.Servers[0].LocalEndPoint);
        Assert.Null(group.Servers[1].LocalEndPoint);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ChannelServerDefinition ScopedDefinition(string name)
        => new(
            name,
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline => pipeline.Run(context =>
            {
                GroupScopedProbe.Instances.Add(
                    context.Services.GetRequiredService<GroupScopedProbe>());
                return Task.CompletedTask;
            }));

    private sealed class GroupScopedProbe : IDisposable
    {
        public static readonly ConcurrentBag<GroupScopedProbe> Instances = new();

        private static int _disposed;

        public static int DisposedCount => Volatile.Read(ref _disposed);

        public static void Reset()
        {
            Instances.Clear();
            Interlocked.Exchange(ref _disposed, 0);
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }

    private static IHost BuildHost(params ChannelServerDefinition[] definitions)
    {
        var builder = Host.CreateApplicationBuilder();

        foreach (var definition in definitions)
            builder.Services.AddChannelServer(definition);

        builder.Services.AddChannelServerGroup();
        return builder.Build();
    }

    private static ChannelServerDefinition EchoDefinition(
        string name,
        int port = 0,
        TimeSpan? shutdownTimeout = null,
        int maxConnections = 0)
        => new(
            name,
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
                ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2),
                MaxConnections = maxConnections,
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));
}
