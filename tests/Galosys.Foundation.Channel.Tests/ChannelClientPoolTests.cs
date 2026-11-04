using System.IO;
using System.Net;
using System.Net.Sockets;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelClientPoolTests
{
    [Fact]
    public async Task RentAsync_ReturnsConnectedClient()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var client = await pool.RentAsync(CancellationToken.None);

        Assert.Equal(ChannelConnectionState.Connected, client.State);
        Assert.NotNull(client.Connection);
        await pool.ReturnAsync(client);
    }

    [Fact]
    public async Task RentAsync_ReusesReturnedClient()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var first = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(first);
        var second = await pool.RentAsync(CancellationToken.None);

        Assert.Same(first, second);
        await pool.ReturnAsync(second);
    }

    [Fact]
    public async Task RentAsync_WaitsWhenPoolFull()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var first = await pool.RentAsync(CancellationToken.None);
        var secondTask = pool.RentAsync(CancellationToken.None).AsTask();

        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        await pool.ReturnAsync(first);
        var second = await secondTask.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Same(first, second);
        await pool.ReturnAsync(second);
    }

    [Fact]
    public async Task RentAsync_TimesOutWhenPoolExhausted()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(
            server.EndPoint,
            maxSize: 1,
            rentTimeout: TimeSpan.FromMilliseconds(100));

        var first = await pool.RentAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(
            () => pool.RentAsync(CancellationToken.None).AsTask());

        Assert.Equal(ChannelConnectionState.Connected, first.State);
        await pool.ReturnAsync(first);
    }

    [Fact]
    public async Task RentAsync_CancellationDuringWait()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var first = await pool.RentAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var secondTask = pool.RentAsync(cts.Token).AsTask();

        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);
        await pool.ReturnAsync(first);
    }

    [Fact]
    public async Task RentAsync_CreationFailureReleasesSlot()
    {
        var endpoint = GetClosedLoopbackEndPoint();
        await using var pool = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = endpoint,
                ConnectTimeout = TimeSpan.FromSeconds(1),
            })
            .AsPool()
            .WithMaxSize(1)
            .WithRentTimeout(TimeSpan.FromSeconds(2))
            .Build();

        var error = await Record.ExceptionAsync(
            () => pool.RentAsync(CancellationToken.None).AsTask());

        Assert.True(error is SocketException or TimeoutException);

        await using var server = await TestServer.StartAsync(
            TestHandlers.EchoAsync,
            options => options.EndPoint = endpoint);

        var client = await pool.RentAsync(CancellationToken.None);
        Assert.Equal(ChannelConnectionState.Connected, client.State);
        await pool.ReturnAsync(client);
    }

    [Fact]
    public async Task RentAsync_ConcurrentRentReturn_DoesNotExceedMaxSize()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var factory = new CountingClientFactory();
        await using var pool = CreatePool(
            server.EndPoint,
            maxSize: 3,
            rentTimeout: TimeSpan.FromSeconds(5),
            factory: factory);

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var client = await pool.RentAsync(CancellationToken.None);
            await Task.Delay(10);
            await pool.ReturnAsync(client);
        });

        await Task.WhenAll(tasks).WaitAsync(TestWaits.DefaultTimeout);

        Assert.InRange(factory.CreatedCount, 1, 3);
    }

    [Fact]
    public async Task ReturnAsync_Throws_WhenClientNull()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => pool.ReturnAsync(null!).AsTask());
    }

    [Fact]
    public async Task ReturnAsync_Throws_WhenClientAlreadyReturned()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var client = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.ReturnAsync(client).AsTask());
    }

    [Fact]
    public async Task ReturnAsync_Throws_WhenClientFromAnotherPool()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var firstPool = CreatePool(server.EndPoint, maxSize: 1);
        await using var secondPool = CreatePool(server.EndPoint, maxSize: 1);

        var client = await firstPool.RentAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondPool.ReturnAsync(client).AsTask());

        await firstPool.ReturnAsync(client);
    }

    [Fact]
    public async Task ReturnAsync_DisposesUnhealthyClient()
    {
        var connections = 0;

        await using var server = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                context.Abort(new IOException("drop-first"));
                return;
            }

            await TestHandlers.EchoAsync(context);
        });

        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var first = await pool.RentAsync(CancellationToken.None);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(first.State != ChannelConnectionState.Connected),
            "客户端未在服务端中止后离开 Connected。");

        await pool.ReturnAsync(first);
        Assert.Equal(ChannelConnectionState.Closed, first.State);

        var second = await pool.RentAsync(CancellationToken.None);
        Assert.NotSame(first, second);
        Assert.Equal(ChannelConnectionState.Connected, second.State);
        await pool.ReturnAsync(second);
    }

    [Fact]
    public async Task RentAsync_ReplacesUnhealthyIdleClient()
    {
        var connections = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                await release.Task;
                context.Abort(new IOException("drop-idle"));
                return;
            }

            await TestHandlers.EchoAsync(context);
        });

        await using var pool = CreatePool(server.EndPoint, maxSize: 1);

        var first = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(first);

        release.TrySetResult();

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(first.State != ChannelConnectionState.Connected),
            "空闲客户端未在服务端中止后离开 Connected。");

        var second = await pool.RentAsync(CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(ChannelConnectionState.Closed, first.State);
        Assert.Equal(ChannelConnectionState.Connected, second.State);
        await pool.ReturnAsync(second);
    }

    [Fact]
    public async Task DisposeAsync_DisposesIdleClients()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var pool = CreatePool(server.EndPoint, maxSize: 1);

        var client = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(client);
        await pool.DisposeAsync();

        Assert.Equal(ChannelConnectionState.Closed, client.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.RentAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DisposeAsync_DoesNotCloseRentedClient()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var pool = CreatePool(server.EndPoint, maxSize: 1);

        var client = await pool.RentAsync(CancellationToken.None);
        await pool.DisposeAsync();

        Assert.Equal(ChannelConnectionState.Connected, client.State);

        await pool.ReturnAsync(client);
        Assert.Equal(ChannelConnectionState.Closed, client.State);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var pool = CreatePool(server.EndPoint, maxSize: 1);

        await pool.DisposeAsync();
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.RentAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RentAsync_WaitingDuringDispose_ThrowsObjectDisposed()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var pool = CreatePool(server.EndPoint, maxSize: 1);

        var first = await pool.RentAsync(CancellationToken.None);
        var secondTask = pool.RentAsync(CancellationToken.None).AsTask();

        await Task.Delay(100);
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => secondTask);
        await pool.ReturnAsync(first);
    }

    [Fact]
    public async Task Constructor_RejectsInvalidOptions()
    {
        // 池配置不再直接构造，改由构建器校验：连接数上限必须大于 0。
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelClientBuilder
            .Create(new ChannelClientOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) })
            .AsPool()
            .WithMaxSize(0));

        // 租借等待超时必须大于 0。
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelClientBuilder
            .Create(new ChannelClientOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) })
            .AsPool()
            .WithRentTimeout(TimeSpan.Zero));
    }

    [Fact]
    public async Task Constructor_UsesInjectedFactory()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var factory = new CountingClientFactory();
        await using var pool = CreatePool(server.EndPoint, maxSize: 1, factory: factory);

        var client = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(client);

        Assert.Equal(1, factory.CreatedCount);
    }

    private static ChannelClientPool CreatePool(
        IPEndPoint endPoint,
        int maxSize,
        TimeSpan? rentTimeout = null,
        IChannelClientFactory? factory = null)
    {
        var builder = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = endPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .AsPool()
            .WithMaxSize(maxSize)
            .WithRentTimeout(rentTimeout ?? TimeSpan.FromSeconds(2));

        if (factory is not null)
            builder = builder.WithFactory(factory);

        return builder.Build();
    }

    private static IPEndPoint GetClosedLoopbackEndPoint()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var endpoint = (IPEndPoint)probe.LocalEndpoint;
        probe.Stop();
        return endpoint;
    }

    private sealed class CountingClientFactory : IChannelClientFactory
    {
        private readonly ChannelClientFactory _inner = new();
        private int _created;

        public int CreatedCount => Volatile.Read(ref _created);

        public IChannelClient Create(ChannelClientOptions options)
        {
            Interlocked.Increment(ref _created);
            return _inner.Create(options);
        }
    }
}
