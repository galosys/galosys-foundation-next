using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelServerGroupTests
{
    [Fact]
    public async Task StartAsync_StartsTwoServers_AndBindsIndependentEndPoints()
    {
        var first = CreateServer();
        var second = CreateServer();
        await using var group = new ChannelServerGroup(first, second);

        await group.StartAsync(CancellationToken.None);

        try
        {
            Assert.Equal(2, group.Servers.Count);
            Assert.NotNull(first.LocalEndPoint);
            Assert.NotNull(second.LocalEndPoint);
            Assert.NotEqual(
                ((IPEndPoint)first.LocalEndPoint!).Port,
                ((IPEndPoint)second.LocalEndPoint!).Port);

            // 两个 server 都必须真的可以接受连接。
            foreach (var server in group.Servers)
            {
                using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
                var stream = client.GetStream();
                await stream.WriteAsync("ping"u8.ToArray());
                var echoed = await TestClient.ReadExactlyAsync(stream, 4);
                Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
            }
        }
        finally
        {
            await group.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_UsesIndependentOptions_AndStopAll_ClosesBoth()
    {
        var first = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(1),
                MaxConnections = 1,
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        var second = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(3),
                MaxConnections = 0,
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await using var group = new ChannelServerGroup(first, second);
        await group.StartAsync(CancellationToken.None);

        var firstEndPoint = (IPEndPoint)first.LocalEndPoint!;
        var secondEndPoint = (IPEndPoint)second.LocalEndPoint!;
        Assert.NotEqual(firstEndPoint.Port, secondEndPoint.Port);

        await group.StopAsync(CancellationToken.None);

        Assert.Null(first.LocalEndPoint);
        Assert.Null(second.LocalEndPoint);
        Assert.False(await TestClient.TryConnectAsync(firstEndPoint));
        Assert.False(await TestClient.TryConnectAsync(secondEndPoint));
    }

    [Fact]
    public async Task StartAsync_SecondBindFails_StopsFirstAndRethrowsOriginal()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var first = CreateServer();
        var second = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, busyPort),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await using var group = new ChannelServerGroup(first, second);

        var error = await Assert.ThrowsAsync<SocketException>(
            () => group.StartAsync(CancellationToken.None));

        Assert.Null(first.LocalEndPoint);
        Assert.Null(second.LocalEndPoint);
        Assert.IsType<SocketException>(error);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndStopsBothServers()
    {
        var first = CreateServer();
        var second = CreateServer();
        var group = new ChannelServerGroup(first, second);

        await group.StartAsync(CancellationToken.None);
        await group.DisposeAsync();
        await group.DisposeAsync();
        group.Dispose();

        Assert.Null(first.LocalEndPoint);
        Assert.Null(second.LocalEndPoint);
    }

    [Fact]
    public async Task EmptyGroup_AllOperationsAreNoOp()
    {
        await using var group = new ChannelServerGroup();

        await group.StartAsync(CancellationToken.None);
        await group.StopAsync(CancellationToken.None);
        await group.DisposeAsync();

        Assert.Empty(group.Servers);
    }

    [Fact]
    public async Task StartAsync_CanceledToken_ThrowsAndLeavesServersUnbound()
    {
        var first = CreateServer();
        var second = CreateServer();
        await using var group = new ChannelServerGroup(first, second);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => group.StartAsync(cts.Token));

        Assert.Null(first.LocalEndPoint);
        Assert.Null(second.LocalEndPoint);
    }

    [Fact]
    public async Task StartAsync_StartsServersInServersOrder()
    {
        var events = new List<string>();
        var first = new FakeServer(onStart: () => events.Add("first"));
        var second = new FakeServer(onStart: () => events.Add("second"));
        await using var group = new ChannelServerGroup(first, second);

        await group.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { "first", "second" }, events);
        Assert.NotNull(first.LocalEndPoint);
        Assert.NotNull(second.LocalEndPoint);
    }

    [Fact]
    public async Task StartAsync_CanceledAfterFirstStart_RollsBackFirst()
    {
        using var cts = new CancellationTokenSource();
        var first = new FakeServer(onStart: cts.Cancel);
        var second = new FakeServer();
        await using var group = new ChannelServerGroup(first, second);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => group.StartAsync(cts.Token));

        Assert.Equal(1, first.StartCalls);
        Assert.Equal(1, first.StopCalls);
        Assert.Null(first.LocalEndPoint);
        Assert.Equal(0, second.StartCalls);
    }

    [Fact]
    public async Task StopAsync_CanceledToken_StillCallsEveryServerAndAggregates()
    {
        var first = new FakeServer(observeCancellation: true);
        var second = new FakeServer(observeCancellation: true);
        var group = new ChannelServerGroup(first, second);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => group.StopAsync(cts.Token));

        // 取消只影响等待结果，不会跳过任何 server。
        Assert.Equal(1, first.StopCalls);
        Assert.Equal(1, second.StopCalls);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(
            error.InnerExceptions,
            exception => Assert.IsAssignableFrom<OperationCanceledException>(exception));
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCalls_DisposeEachServerOnce()
    {
        var first = new FakeServer();
        var second = new FakeServer();
        var group = new ChannelServerGroup(first, second);
        await group.StartAsync(CancellationToken.None);

        await Task.WhenAll(
            group.DisposeAsync().AsTask(),
            group.DisposeAsync().AsTask(),
            group.DisposeAsync().AsTask());

        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task StopAsync_OneServerFails_StopsOthersAndThrowsAggregateException()
    {
        var first = new FakeServer(stopError: new InvalidOperationException("stop-a"));
        var second = new FakeServer();
        var group = new ChannelServerGroup(first, second);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => group.StopAsync(CancellationToken.None));

        // 一个 server 失败不影响其余 server 的停止。
        Assert.Equal(1, first.StopCalls);
        Assert.Equal(1, second.StopCalls);
        Assert.Single(error.InnerExceptions);
        Assert.Contains(error.InnerExceptions, e => e.Message == "stop-a");
    }

    [Fact]
    public async Task StopAsync_TwoServersFail_AggregateContainsBoth()
    {
        var first = new FakeServer(stopError: new InvalidOperationException("stop-a"));
        var second = new FakeServer(stopError: new InvalidOperationException("stop-b"));
        var group = new ChannelServerGroup(first, second);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => group.StopAsync(CancellationToken.None));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, e => e.Message == "stop-a");
        Assert.Contains(error.InnerExceptions, e => e.Message == "stop-b");
    }

    [Fact]
    public async Task DisposeAsync_DisposeFails_StillDisposesRemainingAndAggregates()
    {
        var first = new FakeServer(disposeError: new InvalidOperationException("dispose-a"));
        var second = new FakeServer();
        var group = new ChannelServerGroup(first, second);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => group.DisposeAsync().AsTask());

        // 释放失败后仍然尝试释放其余 server。
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Single(error.InnerExceptions);
        Assert.Contains(error.InnerExceptions, e => e.Message == "dispose-a");

        // 第二次调用观察同一失败，且不会重复释放。
        var secondError = await Assert.ThrowsAsync<AggregateException>(
            () => group.DisposeAsync().AsTask());

        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Single(secondError.InnerExceptions);
        Assert.Contains(secondError.InnerExceptions, e => e.Message == "dispose-a");
    }

    private static ChannelServer CreateServer()
        => new(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

    /// <summary>可控的假服务器：只记录调用并按需抛出异常，用于验证组在失败路径上的编排。</summary>
    private sealed class FakeServer : IChannelServer
    {
        private readonly Exception? _stopError;
        private readonly Exception? _disposeError;
        private readonly Action? _onStart;
        private readonly bool _observeCancellation;
        private int _startCalls;
        private int _stopCalls;
        private int _disposeCalls;

        public FakeServer(
            Exception? stopError = null,
            Exception? disposeError = null,
            Action? onStart = null,
            bool observeCancellation = false)
        {
            _stopError = stopError;
            _disposeError = disposeError;
            _onStart = onStart;
            _observeCancellation = observeCancellation;
        }

        public int StartCalls => Volatile.Read(ref _startCalls);

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public EndPoint? LocalEndPoint { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _startCalls);
            _onStart?.Invoke();
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            return Task.CompletedTask;
        }

        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stopCalls);
            LocalEndPoint = null;

            if (_observeCancellation)
                ct.ThrowIfCancellationRequested();

            return _stopError is null ? Task.CompletedTask : Task.FromException(_stopError);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return _disposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(_disposeError);
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
