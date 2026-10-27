using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelServerTests
{
    [Fact]
    public async Task StartAsync_BindsAndEchoes()
    {
        var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await server.StartAsync(CancellationToken.None);

        try
        {
            var endPoint = (IPEndPoint)server.LocalEndPoint!;
            using (var client = await TestClient.ConnectAsync(endPoint))
            {
                var stream = client.GetStream();

                await stream.WriteAsync("ping"u8.ToArray());
                var echoed = await TestClient.ReadExactlyAsync(stream, 4);

                Assert.Equal("ping", Encoding.UTF8.GetString(echoed));
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ThrowsSocketException_WhenPortInUse()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var server = new ChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, port) },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await Assert.ThrowsAsync<SocketException>(() => server.StartAsync(CancellationToken.None));
        Assert.Null(server.LocalEndPoint);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Throws_WhenPipelineHasNoRun()
    {
        var server = new ChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(CancellationToken.None));
        Assert.Null(server.LocalEndPoint);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task Instances_UseIndependentOptions()
    {
        var firstOptions = new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            MaxConnections = 1,
        };
        var secondOptions = new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            MaxConnections = 2,
        };

        var first = new ChannelServer(firstOptions, pipeline => pipeline.Run(TestHandlers.EchoAsync));
        var second = new ChannelServer(secondOptions, pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);

        try
        {
            var firstEndPoint = (IPEndPoint)first.LocalEndPoint!;
            var secondEndPoint = (IPEndPoint)second.LocalEndPoint!;

            Assert.NotEqual(firstEndPoint.Port, secondEndPoint.Port);

            await first.StopAsync(CancellationToken.None);
            Assert.Null(first.LocalEndPoint);
            Assert.NotNull(second.LocalEndPoint);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Options_AreClonedAtConstruction()
    {
        var endPoint = new IPEndPoint(IPAddress.Loopback, 0);
        var options = new ChannelServerOptions
        {
            EndPoint = endPoint,
        };
        var server = new ChannelServer(options, pipeline => pipeline.Run(TestHandlers.EchoAsync));

        endPoint.Port = 1;

        await server.StartAsync(CancellationToken.None);

        try
        {
            Assert.NotEqual(1, ((IPEndPoint)server.LocalEndPoint!).Port);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_Cancel_StopsServer()
    {
        using var cts = new CancellationTokenSource();
        var server = new ChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        var runTask = server.RunAsync(cts.Token);
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(server.LocalEndPoint is not null),
            "服务器未在超时时间内绑定。");

        cts.Cancel();
        await runTask.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Null(server.LocalEndPoint);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PipelineConfiguration_IsApplied()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new ChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use((context, next) =>
                {
                    invoked.TrySetResult();
                    return next(context);
                });
                pipeline.Run(_ => Task.CompletedTask);
            });

        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
            await invoked.Task.WaitAsync(TestWaits.DefaultTimeout);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
