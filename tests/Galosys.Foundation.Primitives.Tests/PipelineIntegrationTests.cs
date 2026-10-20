using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PipelineIntegrationTests
{
    [Fact]
    public async Task Middleware_RunsInOrder_AroundHandler()
    {
        var events = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use(async (context, next) =>
                {
                    events.Enqueue("A-in");
                    await next(context);
                    events.Enqueue("A-out");
                    completed.TrySetResult();
                });
                pipeline.Use(async (context, next) =>
                {
                    events.Enqueue("B-in");
                    await next(context);
                    events.Enqueue("B-out");
                });
                pipeline.Run(_ =>
                {
                    events.Enqueue("handler");
                    return Task.CompletedTask;
                });
            },
            _ => Task.CompletedTask);

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        using (var client = await TestClient.ConnectAsync(endPoint))
        {
            await TestClient.ReadOneByteAsync(client.GetStream());
        }

        await completed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.Equal(new[] { "A-in", "B-in", "handler", "B-out", "A-out" }, events);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task ShortCircuitMiddleware_SkipsHandler()
    {
        var handlerCalled = false;
        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use((context, next) => Task.CompletedTask);
                pipeline.Run(_ =>
                {
                    handlerCalled = true;
                    return Task.CompletedTask;
                });
            },
            _ => Task.CompletedTask);

        await server.StartAsync(CancellationToken.None);
        await using var handle = new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            Assert.Equal(0, await TestClient.ReadOneByteAsync(client.GetStream()));
        }

        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Configure_WithTwoRunCalls_Throws()
    {
        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Run(_ => Task.CompletedTask);
                pipeline.Run(_ => Task.CompletedTask);
            },
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(CancellationToken.None));
    }
}
