using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

/// <summary>测试用的内存连接；只实现会话写路径需要的成员。</summary>
internal sealed class TestChannelConnection : IChannelConnection
{
    private readonly TaskCompletionSource<Exception?> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestChannelConnection(IDuplexPipe transport) => Transport = transport;

    public EndPoint? LocalEndPoint => null;

    public EndPoint? RemoteEndPoint => null;

    public IDuplexPipe Transport { get; }

    public Task<Exception?> Termination => _termination.Task;

    public Task Completion => _completion.Task;

    public bool Aborted { get; private set; }

    public Exception? AbortReason { get; private set; }

    public void Abort(Exception? reason)
    {
        Aborted = true;
        AbortReason ??= reason;
        _termination.TrySetResult(reason);
        _completion.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        _completion.TrySetResult();

        try
        {
            Transport.Output.Complete();
        }
        catch (Exception)
        {
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>统计 flush 次数的写端包装；其余行为原样转发。</summary>
internal sealed class CountingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private int _flushCount;

    public CountingPipeWriter(PipeWriter inner) => _inner = inner;

    public int FlushCount => Volatile.Read(ref _flushCount);

    public override void Advance(int bytes) => _inner.Advance(bytes);

    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _flushCount);
        return _inner.FlushAsync(ct);
    }
}

/// <summary>flush 一直等待取消的写端；用于验证取消语义。</summary>
internal sealed class BlockingFlushPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;

    public BlockingFlushPipeWriter(PipeWriter inner) => _inner = inner;

    public override void Advance(int bytes) => _inner.Advance(bytes);

    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        return new FlushResult(false, false);
    }
}

/// <summary>在指定消息上编码失败的 codec。</summary>
internal sealed class FailingCodec : IMessageCodec<int>
{
    private readonly int _failOn;

    public FailingCodec(int failOn) => _failOn = failOn;

    public int Decode(ReadOnlySequence<byte> frame) => throw new NotSupportedException();

    public void Encode(IBufferWriter<byte> writer, int message)
    {
        if (message == _failOn)
            throw new InvalidOperationException("encode-boom");

        writer.Write(BitConverter.GetBytes(message));
    }
}

/// <summary>写入固定长度 payload 的 codec；用于触发消息超限。</summary>
internal sealed class FixedSizeCodec : IMessageCodec<int>
{
    private readonly int _size;

    public FixedSizeCodec(int size) => _size = size;

    public int Decode(ReadOnlySequence<byte> frame) => throw new NotSupportedException();

    public void Encode(IBufferWriter<byte> writer, int message)
    {
        var span = writer.GetSpan(_size);
        span.Slice(0, _size).Fill((byte)message);
        writer.Advance(_size);
    }
}

/// <summary>记录写入顺序的自定义会话；用于验证扩展的回退路径。</summary>
internal sealed class RecordingSession<T> : IChannelSession<T>
{
    public List<T> Written { get; } = new();

    public CancellationToken LastToken { get; private set; }

    public ValueTask<T> ReadAsync(CancellationToken ct) => throw new NotSupportedException();

    public ValueTask WriteAsync(T message, CancellationToken ct)
    {
        Written.Add(message);
        LastToken = ct;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
