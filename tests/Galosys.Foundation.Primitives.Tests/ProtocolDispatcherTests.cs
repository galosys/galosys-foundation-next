using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ProtocolDispatcherTests
{
    [Fact]
    public async Task HandleAsync_CallsSelectorOnce_ThenProtocol()
    {
        var protocol = new RecordingProtocol();
        var selector = new RecordingSelector(protocol);
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "dispatcher-test",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(1, protocol.Calls);
        Assert.Equal(
            new[] { "select", "handle" },
            selector.Events.Concat(protocol.Events));
        Assert.Equal(context.ConnectionClosed, protocol.Token);
    }

    [Fact]
    public async Task HandleAsync_NoMatch_AbortsWithInvalidDataException()
    {
        var selector = new RecordingSelector();
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "no-match",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Equal(1, selector.Calls);
        Assert.IsType<InvalidDataException>(context.AbortReason);
        Assert.IsType<InvalidDataException>(
            await connection.Termination.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProtocolDispatcher_Constructor_NullSelector_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProtocolDispatcher(null!));
    }

    [Fact]
    public async Task HandleAsync_SelectorThrows_WrapsInnerException()
    {
        var inner = new IOException("selector-boom");
        var selector = new RecordingSelector(error: inner);
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "selector-error",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        var error = Assert.IsType<InvalidDataException>(context.AbortReason);
        Assert.Same(inner, error.InnerException);
    }

    [Fact]
    public async Task HandleAsync_SelectorThrowsInvalidData_DoesNotDoubleWrap()
    {
        var original = new InvalidDataException("bad-prefix");
        var selector = new RecordingSelector(error: original);
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "selector-invalid-data",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Same(original, context.AbortReason);
    }

    [Fact]
    public async Task HandleAsync_ConnectionClosedBeforeDispatch_SkipsSelector()
    {
        var selector = new RecordingSelector();
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "already-closed",
            connection,
            EmptyServiceProvider.Instance);

        context.Abort();

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Equal(0, selector.Calls);
        Assert.Null(context.AbortReason);
    }

    [Fact]
    public async Task HandleAsync_SelectorObservesCancellation_DoesNotAbort()
    {
        var selector = new AbortingSelector();
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "cancel-during-select",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Equal(1, selector.Calls);
        Assert.Null(context.AbortReason);
    }

    [Fact]
    public async Task HandleAsync_UnknownProtocol_LeavesInputUnconsumed()
    {
        var connection = new FakeChannelConnection();
        await connection.Writer.WriteAsync(Encoding.UTF8.GetBytes("XYZ"));
        await connection.Writer.FlushAsync();

        await using var context = new ChannelContext(
            "unknown-protocol",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(new PeekThenNullSelector());
        await dispatcher.HandleAsync(context);

        Assert.IsType<InvalidDataException>(context.AbortReason);

        var result = await context.Reader.ReadAsync();
        Assert.Equal("XYZ", Encoding.UTF8.GetString(result.Buffer.ToArray()));
        context.Reader.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task HandleAsync_ConnectionClosedAfterSelection_SkipsProtocol()
    {
        var protocol = new RecordingProtocol();
        var selector = new ClosingSelector(protocol);
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "closed-after-select",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);
        await dispatcher.HandleAsync(context);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(0, protocol.Calls);
        Assert.Null(context.AbortReason);
    }

    [Fact]
    public async Task HandleAsync_ProtocolThrows_Propagates()
    {
        var selector = new RecordingSelector(
            new ThrowingProtocol(new InvalidOperationException("protocol-boom")));
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "protocol-error",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.HandleAsync(context));

        Assert.Null(context.AbortReason);
    }

    [Fact]
    public async Task HandleAsync_WorksAsChannelHandlerDelegate()
    {
        var protocol = new RecordingProtocol();
        var selector = new RecordingSelector(protocol);
        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "handler-delegate",
            connection,
            EmptyServiceProvider.Instance);

        var dispatcher = new ProtocolDispatcher(selector);

        // 方法组必须能直接转换为 ChannelHandlerDelegate，编译期即锁定签名。
        ChannelHandlerDelegate handler = dispatcher.HandleAsync;
        await handler(context);

        Assert.Equal(1, selector.Calls);
        Assert.Equal(1, protocol.Calls);
    }

    [Fact]
    public async Task HandleAsync_SelectionLeavesTransportUsable()
    {
        var connection = new FakeChannelConnection();
        await connection.Writer.WriteAsync(Encoding.UTF8.GetBytes("HELLO"));
        await connection.Writer.FlushAsync();

        await using var context = new ChannelContext(
            "transport-usable",
            connection,
            EmptyServiceProvider.Instance);

        var transport = context.Transport;
        var protocol = new WritingProtocol("OK");
        var dispatcher = new ProtocolDispatcher(new PeekThenReturnSelector(protocol));

        await dispatcher.HandleAsync(context);

        // 选择器只窥探，协议写入后传输实例与读写端都应保持不变且可用。
        Assert.Equal(1, protocol.Calls);
        Assert.Same(transport, context.Transport);
        Assert.Same(transport.Input, context.Reader);
        Assert.Same(transport.Output, context.Writer);

        await context.Writer.WriteAsync(Encoding.UTF8.GetBytes("!"));
        await context.Writer.FlushAsync();
    }
}

internal sealed class FakeChannelConnection : IChannelConnection
{
    private readonly Pipe _pipe = new();
    private readonly TaskCompletionSource<Exception?> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 0);

    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 0);

    public IDuplexPipe Transport => new DuplexPipe(_pipe.Reader, _pipe.Writer);

    public Task<Exception?> Termination => _termination.Task;

    public PipeWriter Writer => _pipe.Writer;

    public void Abort(Exception? reason)
    {
        _termination.TrySetResult(reason);
        _pipe.Writer.Complete();
    }

    public ValueTask DisposeAsync()
    {
        _termination.TrySetResult(null);
        _pipe.Writer.Complete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingProtocol : IChannelProtocol
{
    public string Name => "recording";

    public int Calls { get; private set; }

    public CancellationToken Token { get; private set; }

    public List<string> Events { get; } = new();

    public ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken)
    {
        Calls++;
        Token = cancellationToken;
        Events.Add("handle");
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingSelector : IChannelProtocolSelector
{
    private readonly IChannelProtocol? _protocol;
    private readonly Exception? _error;

    public RecordingSelector(IChannelProtocol? protocol = null, Exception? error = null)
    {
        _protocol = protocol;
        _error = error;
    }

    public int Calls { get; private set; }

    public List<string> Events { get; } = new();

    public ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        Events.Add("select");

        if (_error is not null)
            throw _error;

        return ValueTask.FromResult<IChannelProtocol?>(_protocol);
    }
}

internal sealed class AbortingSelector : IChannelProtocolSelector
{
    public int Calls { get; private set; }

    public ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        context.Abort();
        throw new OperationCanceledException(context.ConnectionClosed);
    }
}

internal sealed class PeekThenNullSelector : IChannelProtocolSelector
{
    public async ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        var result = await context.Reader.ReadAsync(cancellationToken);
        context.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        return null;
    }
}

internal sealed class PeekThenReturnSelector : IChannelProtocolSelector
{
    private readonly IChannelProtocol _protocol;

    public PeekThenReturnSelector(IChannelProtocol protocol) => _protocol = protocol;

    public async ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        var result = await context.Reader.ReadAsync(cancellationToken);
        context.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        return _protocol;
    }
}

internal sealed class ClosingSelector : IChannelProtocolSelector
{
    private readonly IChannelProtocol _protocol;

    public ClosingSelector(IChannelProtocol protocol) => _protocol = protocol;

    public int Calls { get; private set; }

    public ValueTask<IChannelProtocol?> SelectAsync(
        ChannelContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        context.Abort();
        return ValueTask.FromResult<IChannelProtocol?>(_protocol);
    }
}

internal sealed class ThrowingProtocol : IChannelProtocol
{
    private readonly Exception _error;

    public ThrowingProtocol(Exception error) => _error = error;

    public string Name => "throwing";

    public ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken)
        => throw _error;
}

internal sealed class WritingProtocol : IChannelProtocol
{
    private readonly string _payload;

    public WritingProtocol(string payload) => _payload = payload;

    public string Name => "writing";

    public int Calls { get; private set; }

    public async ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken)
    {
        Calls++;
        await context.Writer.WriteAsync(Encoding.UTF8.GetBytes(_payload), cancellationToken);
        await context.Writer.FlushAsync(cancellationToken);
    }
}
