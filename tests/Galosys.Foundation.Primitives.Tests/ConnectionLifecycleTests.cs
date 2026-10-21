using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ConnectionLifecycleTests
{
    [Fact]
    public async Task EachConnection_GetsUniqueId_AndMatchingRemoteEndPoint()
    {
        var ids = new ConcurrentBag<string>();
        var remotes = new ConcurrentBag<EndPoint?>();
        await using var handle = await TestServer.StartAsync(context =>
        {
            ids.Add(context.ConnectionId);
            remotes.Add(context.RemoteEndPoint);
            return Task.CompletedTask;
        });

        var clientEndPoints = new List<EndPoint?>();

        using (var first = await TestClient.ConnectAsync(handle.EndPoint))
        {
            clientEndPoints.Add(first.Client.LocalEndPoint);
            await TestClient.ReadOneByteAsync(first.GetStream());
        }

        using (var second = await TestClient.ConnectAsync(handle.EndPoint))
        {
            clientEndPoints.Add(second.Client.LocalEndPoint);
            await TestClient.ReadOneByteAsync(second.GetStream());
        }

        await WaitForAsync(() => ids.Count == 2);

        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));

        foreach (var expected in clientEndPoints)
            Assert.Contains(remotes, actual => SameEndPoint(expected, actual));
    }

    private static bool SameEndPoint(EndPoint? left, EndPoint? right)
    {
        if (left is not IPEndPoint first || right is not IPEndPoint second)
            return false;

        return first.Port == second.Port
            && first.Address.MapToIPv4().Equals(second.Address.MapToIPv4());
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
    public async Task PeerDisconnect_FiresConnectionClosedOnce_AndReleasesScope()
    {
        var factory = new TrackingScopeFactory();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedCount = 0;

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                context.ConnectionClosed.Register(() =>
                {
                    Interlocked.Increment(ref closedCount);
                    closed.TrySetResult();
                });

                await TestHandlers.EchoAsync(context);
            },
            scopeFactory: factory);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            client.Client.LingerState = new LingerOption(true, 0);
            client.Close();
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        await factory.Disposed.Task.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.Equal(1, factory.DisposedCount);
    }

    [Fact]
    public async Task PeerReset_NotifiesIdleHandler_AndReleasesScope()
    {
        var factory = new TrackingScopeFactory();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedCount = 0;

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                factory.DisposeProbe = () => context.ConnectionClosed.IsCancellationRequested;
                context.ConnectionClosed.Register(() =>
                {
                    factory.Record("closed");
                    Interlocked.Increment(ref closedCount);
                    closed.TrySetResult();
                });

                ready.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.ConnectionClosed);
            },
            scopeFactory: factory);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await ready.Task.WaitAsync(TestWaits.DefaultTimeout);
            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        await factory.Disposed.Task.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.True(factory.ProbeResult);
        Assert.Equal(1, factory.Events.Count(e => e == "closed"));
        Assert.Equal(1, factory.DisposedCount);
    }

    [Fact]
    public async Task HandlerException_AbortsOnlyThatConnection_AndServerKeepsServing()
    {
        var first = 1;
        ChannelContext? failingContext = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                failingContext = context;
                context.ConnectionClosed.Register(() => closed.TrySetResult());
                throw new InvalidOperationException("boom");
            }

            await TestHandlers.EchoAsync(context);
        });

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
    public async Task Handler_CanAbortConnection_AndAbortPathReleasesScopeAfterClosed()
    {
        var reason = new InvalidOperationException("abort");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TrackingScopeFactory();
        ChannelContext? captured = null;

        await using var handle = await TestServer.StartAsync(
            context =>
            {
                captured = context;
                context.ConnectionClosed.Register(() =>
                {
                    factory.Record("closed");
                    closed.TrySetResult();
                });
                context.Abort(reason);
                return Task.CompletedTask;
            },
            scopeFactory: factory);

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            await TestClient.AssertConnectionClosedAsync(client.GetStream());
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        await factory.Disposed.Task.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Same(reason, captured!.AbortReason);
        Assert.Equal(new[] { "closed", "scope-disposed" }, factory.Events);
    }

    [Fact]
    public async Task Handler_AbortWithoutReason_DoesNotFlushBufferedOutput()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChannelContext? captured = null;

        await using var handle = await TestServer.StartAsync(context =>
        {
            captured = context;

            context.Writer.GetSpan(1)[0] = (byte)'x';
            context.Writer.Advance(1);

            context.ConnectionClosed.Register(() => closed.TrySetResult());
            context.Abort();
            return Task.CompletedTask;
        });

        using (var client = await TestClient.ConnectAsync(handle.EndPoint))
        {
            var receivedPayload = false;

            try
            {
                receivedPayload = await TestClient.ReadOneByteAsync(client.GetStream()) > 0;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
            }

            Assert.False(receivedPayload, "中止后的连接不得刷新缓冲输出。");
        }

        await closed.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.Null(captured!.AbortReason);
    }

    [Fact]
    public async Task RemoteEndPoint_MatchesConnectedClient()
    {
        var seen = new TaskCompletionSource<EndPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await TestServer.StartAsync(context =>
        {
            seen.TrySetResult(context.RemoteEndPoint);
            return Task.CompletedTask;
        });

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var remote = await seen.Task.WaitAsync(TestWaits.DefaultTimeout);

        var expected = Assert.IsType<IPEndPoint>(client.Client.LocalEndPoint);
        var actual = Assert.IsType<IPEndPoint>(remote);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.Address.MapToIPv4(), actual.Address.MapToIPv4());
    }

    [Fact]
    public async Task EachConnection_GetsIndependentScope()
    {
        var factory = new TrackingScopeFactory();
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync, scopeFactory: factory);

        for (var i = 0; i < 2; i++)
        {
            using var client = await TestClient.ConnectAsync(handle.EndPoint);
            var stream = client.GetStream();
            await stream.WriteAsync("x"u8.ToArray());
            await TestClient.ReadExactlyAsync(stream, 1);
        }

        await WaitForAsync(() => factory.DisposedCount == 2);

        Assert.Equal(2, Volatile.Read(ref factory.CreatedCount));
        Assert.Equal(2, factory.DisposedCount);
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
