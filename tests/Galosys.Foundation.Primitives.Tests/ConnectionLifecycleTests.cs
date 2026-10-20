using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ConnectionLifecycleTests
{
    [Fact]
    public async Task EachConnection_GetsUniqueId()
    {
        var ids = new ConcurrentBag<string>();
        await using var handle = await TestServer.StartAsync(context =>
        {
            ids.Add(context.ConnectionId);
            return Task.CompletedTask;
        });

        using (var first = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.ReadOneByteAsync(first.GetStream());
        }

        using (var second = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.ReadOneByteAsync(second.GetStream());
        }

        await WaitForAsync(() => ids.Count == 2);

        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public async Task ConnectionClosed_FiresOnce_BeforeScopeDisposal()
    {
        var factory = new TrackingScopeFactory();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedCount = 0;

        await using var handle = await TestServer.StartAsync(
            context =>
            {
                context.ConnectionClosed.Register(() =>
                {
                    factory.Record("closed");
                    Interlocked.Increment(ref closedCount);
                    closed.TrySetResult();
                });
                return Task.CompletedTask;
            },
            scopeFactory: factory);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.ReadOneByteAsync(client.GetStream());
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        await factory.Disposed.Task.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.Equal(new[] { "closed", "scope-disposed" }, factory.Events);
        Assert.Equal(1, Volatile.Read(ref factory.CreatedCount));
    }

    [Fact]
    public async Task PeerDisconnect_TriggersConnectionClosed()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handle = await TestServer.StartAsync(async context =>
        {
            context.ConnectionClosed.Register(() => closed.TrySetResult());
            await TestHandlers.EchoAsync(context);
        });

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            client.Close();
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.True(closed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HandlerException_AbortsOnlyThatConnection_AndServerKeepsServing()
    {
        var first = 1;
        await using var handle = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
                throw new InvalidOperationException("boom");

            await TestHandlers.EchoAsync(context);
        });

        using (var failing = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.AssertConnectionClosedAsync(failing.GetStream());
        }

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = second.GetStream();
        await stream.WriteAsync("ok"u8.ToArray());
        Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(await TestClient.ReadExactlyAsync(stream, 2)));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestWaits.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("条件在超时时间内未满足。");

            await Task.Delay(10);
        }
    }
}
