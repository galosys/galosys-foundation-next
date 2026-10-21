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
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use((context, next) =>
                {
                    context.ConnectionClosed.Register(() => closed.TrySetResult());
                    return Task.CompletedTask;
                });
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

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Middleware_CanReplaceTransport()
    {
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use((context, next) =>
                {
                    context.UseTransport(new PassThroughDuplexPipe(context.Transport));
                    return next(context);
                });
                pipeline.Run(async context =>
                {
                    if (context.Transport is PassThroughDuplexPipe)
                        replaced.TrySetResult();

                    await TestHandlers.EchoAsync(context);
                });
            },
            _ => Task.CompletedTask);

        await server.StartAsync(CancellationToken.None);
        await using var handle = new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();
        await stream.WriteAsync("tls"u8.ToArray());
        var echoed = await TestClient.ReadExactlyAsync(stream, 3);

        await replaced.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.Equal("tls", System.Text.Encoding.UTF8.GetString(echoed));
    }

    [Fact]
    public async Task MiddlewareException_AbortsConnection_AndServerKeepsServing()
    {
        var first = 1;
        ChannelContext? failingContext = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new ConfiguredTestServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline =>
            {
                pipeline.Use(async (context, next) =>
                {
                    if (Interlocked.Exchange(ref first, 0) == 1)
                    {
                        failingContext = context;
                        context.ConnectionClosed.Register(() => closed.TrySetResult());
                        throw new InvalidOperationException("middleware-boom");
                    }

                    await next(context);
                });
                pipeline.Run(TestHandlers.EchoAsync);
            },
            _ => Task.CompletedTask);

        await server.StartAsync(CancellationToken.None);
        await using var handle = new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);

        using (var failing = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.AssertConnectionClosedAsync(failing.GetStream());
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.IsType<InvalidOperationException>(failingContext!.AbortReason);

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = second.GetStream();
        await stream.WriteAsync("ok"u8.ToArray());
        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(await TestClient.ReadExactlyAsync(stream, 2)));
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
