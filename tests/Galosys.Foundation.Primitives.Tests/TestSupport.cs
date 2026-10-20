using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

internal static class TestWaits
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
}

internal sealed class TestChannelServer : ChannelServerBase
{
    private readonly Func<ChannelContext, Task> _handler;

    public TestChannelServer(ChannelServerOptions options, Func<ChannelContext, Task> handler)
        : base(options)
    {
        _handler = handler;
    }

    protected override Task ChannelHandleAsync(ChannelContext context) => _handler(context);
}

internal sealed class ConfiguredTestServer : ChannelServerBase
{
    private readonly Action<IChannelPipeline> _configure;
    private readonly Func<ChannelContext, Task> _handler;

    public ConfiguredTestServer(
        ChannelServerOptions options,
        Action<IChannelPipeline> configure,
        Func<ChannelContext, Task> handler)
        : base(options)
    {
        _configure = configure;
        _handler = handler;
    }

    protected override void Configure(IChannelPipeline pipeline) => _configure(pipeline);

    protected override Task ChannelHandleAsync(ChannelContext context) => _handler(context);
}

internal sealed class TestServerHandle : IAsyncDisposable
{
    public TestServerHandle(ChannelServerBase server, IPEndPoint endPoint)
    {
        Server = server;
        EndPoint = endPoint;
    }

    public ChannelServerBase Server { get; }

    public IPEndPoint EndPoint { get; }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync().ConfigureAwait(false);
}

internal static class TestServer
{
    public static async Task<TestServerHandle> StartAsync(
        Func<ChannelContext, Task> handler,
        Action<ChannelServerOptions>? configure = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var options = new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        };
        configure?.Invoke(options);

        var server = new TestChannelServer(options, handler)
        {
            ScopeFactory = scopeFactory,
        };

        await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return new TestServerHandle(server, (IPEndPoint)server.LocalEndPoint!);
    }
}

internal static class TestClient
{
    public static async Task<TcpClient> ConnectAsync(IPEndPoint endPoint)
    {
        var client = new TcpClient();
        await client.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TestWaits.DefaultTimeout);
        return client;
    }

    public static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset)).AsTask().WaitAsync(TestWaits.DefaultTimeout);
            if (read == 0)
                throw new IOException("连接在读取完成前关闭。");

            offset += read;
        }

        return buffer;
    }

    public static Task<int> ReadOneByteAsync(NetworkStream stream)
        => stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TestWaits.DefaultTimeout);

    public static async Task AssertConnectionClosedAsync(NetworkStream stream)
    {
        try
        {
            Assert.Equal(0, await ReadOneByteAsync(stream));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    public static async Task WaitUntilListeningAsync(ChannelServerBase server)
    {
        var deadline = DateTime.UtcNow + TestWaits.DefaultTimeout;
        while (server.LocalEndPoint is null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("服务器未在超时时间内完成绑定。");

            await Task.Delay(10);
        }
    }
}

internal static class TestHandlers
{
    public static async Task EchoAsync(ChannelContext context)
    {
        while (true)
        {
            var result = await context.Reader.ReadAsync();
            if (result.IsCompleted && result.Buffer.IsEmpty)
                break;

            foreach (var segment in result.Buffer)
                await context.Writer.WriteAsync(segment);

            context.Reader.AdvanceTo(result.Buffer.End);
            await context.Writer.FlushAsync();
        }
    }

    public static async Task HoldAsync(ChannelContext context, CancellationToken release)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(release, context.ConnectionClosed);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal sealed class TrackingScopeFactory : IServiceScopeFactory
{
    private readonly ConcurrentQueue<string> _events = new();

    public int CreatedCount;

    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<string> Events => _events;

    public IServiceScope CreateScope()
    {
        Interlocked.Increment(ref CreatedCount);
        return new Scope(this);
    }

    public void Record(string value) => _events.Enqueue(value);

    private void NotifyDisposed()
    {
        _events.Enqueue("scope-disposed");
        Disposed.TrySetResult();
    }

    private sealed class Scope : IServiceScope, IAsyncDisposable
    {
        private readonly TrackingScopeFactory _owner;

        public Scope(TrackingScopeFactory owner) => _owner = owner;

        public IServiceProvider ServiceProvider { get; } = new EmptyProvider();

        public void Dispose() => _owner.NotifyDisposed();

        public ValueTask DisposeAsync()
        {
            _owner.NotifyDisposed();
            return ValueTask.CompletedTask;
        }

        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
