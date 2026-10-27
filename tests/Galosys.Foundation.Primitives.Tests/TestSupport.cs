using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

internal static class TestWaits
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
}

internal sealed class TestChannelServer : IChannelServer
{
    private readonly ChannelServer _server;

    public TestChannelServer(
        ChannelServerOptions options,
        Func<ChannelContext, Task> handler,
        IServiceScopeFactory? scopeFactory = null)
    {
        _server = new ChannelServer(
            options,
            pipeline => pipeline.Run(context => handler(context)),
            loggerFactory: null,
            scopeFactory: scopeFactory);
    }

    public EndPoint? LocalEndPoint => _server.LocalEndPoint;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task RunAsync(CancellationToken ct) => _server.RunAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(ct);

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    public void Dispose() => _server.Dispose();
}

internal sealed class ConfiguredTestServer : IChannelServer
{
    private readonly ChannelServer _server;

    public ConfiguredTestServer(
        ChannelServerOptions options,
        Action<IChannelPipeline> configure,
        Func<ChannelContext, Task>? handler = null)
    {
        // 保留旧构造签名；终端处理器由 configure 中的 Run 决定。
        _ = handler;
        _server = new ChannelServer(options, configure);
    }

    public EndPoint? LocalEndPoint => _server.LocalEndPoint;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task RunAsync(CancellationToken ct) => _server.RunAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(ct);

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    public void Dispose() => _server.Dispose();
}

internal sealed class TestChannelServerRunner : IHostedService
{
    private readonly ChannelServer _server;

    public TestChannelServerRunner(ChannelServer server) => _server = server;

    public Task StartAsync(CancellationToken ct) => _server.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _server.StopAsync(CancellationToken.None);
}

internal sealed class TestServerHandle : IAsyncDisposable
{
    public TestServerHandle(IChannelServer server, IPEndPoint endPoint)
    {
        Server = server;
        EndPoint = endPoint;
    }

    public IChannelServer Server { get; }

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

        var server = new TestChannelServer(options, handler, scopeFactory);

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

    public static async Task AssertConnectFailsAsync(IPEndPoint endPoint)
    {
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TestWaits.DefaultTimeout);
        });
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow + TestWaits.DefaultTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message);

            await Task.Delay(10);
        }
    }

    public static async Task<bool> TryConnectAsync(IPEndPoint endPoint)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }

    public static async Task WaitUntilListeningAsync(IChannelServer server)
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

internal sealed class PassThroughDuplexPipe : IDuplexPipe
{
    public PassThroughDuplexPipe(IDuplexPipe inner)
    {
        Input = inner.Input;
        Output = inner.Output;
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }
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

internal sealed class ThrowingOnceScopeFactory : IServiceScopeFactory
{
    private int _calls;

    public int CreatedCount;

    public IServiceScope CreateScope()
    {
        if (Interlocked.Increment(ref _calls) == 1)
            throw new InvalidOperationException("scope-boom");

        Interlocked.Increment(ref CreatedCount);
        return EmptyServiceScope.Instance;
    }
}

internal sealed class TrackingScopeFactory : IServiceScopeFactory
{
    private readonly ConcurrentQueue<string> _events = new();

    public int CreatedCount;

    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<bool>? DisposeProbe { get; set; }

    public bool? ProbeResult { get; private set; }

    public IReadOnlyCollection<string> Events => _events;

    public int DisposedCount => _events.Count(e => e == "scope-disposed");

    public IServiceScope CreateScope()
    {
        Interlocked.Increment(ref CreatedCount);
        return new Scope(this);
    }

    public void Record(string value) => _events.Enqueue(value);

    private void NotifyDisposed()
    {
        ProbeResult = DisposeProbe?.Invoke();
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
            public object? GetService(Type serviceType)
                => serviceType == typeof(IServiceProvider) ? this : null;
        }
    }
}
