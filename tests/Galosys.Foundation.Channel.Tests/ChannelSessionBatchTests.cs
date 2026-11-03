using System.IO;
using System.IO.Pipelines;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelSessionBatchTests
{
    [Fact]
    public async Task WriteBatchAsync_EmptyBatch_DoesNothing()
    {
        var pipe = new Pipe();
        var writer = new CountingPipeWriter(pipe.Writer);
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        await session.WriteBatchAsync(Array.Empty<int>());

        Assert.Equal(0, writer.FlushCount);
    }

    [Fact]
    public async Task WriteBatchAsync_Throws_WhenSessionNull()
    {
        IChannelSession<int>? session = null;

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session!.WriteBatchAsync(new[] { 1 }).AsTask());
    }

    [Fact]
    public async Task WriteBatchAsync_Throws_WhenMessagesNull()
    {
        var pipe = new Pipe();
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, pipe.Writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.WriteBatchAsync(null!).AsTask());
    }

    [Fact]
    public async Task WriteBatchAsync_CustomSession_WritesSequentially()
    {
        var session = new RecordingSession<int>();
        using var cts = new CancellationTokenSource();

        await session.WriteBatchAsync(new[] { 1, 2, 3 }, cts.Token);

        Assert.Equal(new[] { 1, 2, 3 }, session.Written);
        Assert.Equal(cts.Token, session.LastToken);
    }

    [Fact]
    public async Task WriteBatchAsync_WritesAllFramesAndFlushesOnce()
    {
        var pipe = new Pipe();
        var writer = new CountingPipeWriter(pipe.Writer);
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        await session.WriteBatchAsync(new[] { 1, 2, 3 });

        Assert.Equal(1, writer.FlushCount);
        Assert.Equal(new[] { 1, 2, 3 }, await ReadIntsAsync(session, 3));
    }

    [Fact]
    public async Task WriteBatchAsync_ProducesSameBytesAsSingleWrites()
    {
        var singlePipe = new Pipe();
        var batchPipe = new Pipe();

        var singleConnection = new TestChannelConnection(
            new DuplexPipe(singlePipe.Reader, singlePipe.Writer));
        var singleContext = new ChannelContext("single", singleConnection, EmptyServiceProvider.Instance);
        await using var singleSession = singleContext.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        foreach (var message in new[] { 1, 2, 3 })
            await singleSession.WriteAsync(message, CancellationToken.None);

        var batchConnection = new TestChannelConnection(
            new DuplexPipe(batchPipe.Reader, batchPipe.Writer));
        var batchContext = new ChannelContext("batch", batchConnection, EmptyServiceProvider.Instance);
        await using var batchSession = batchContext.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        await batchSession.WriteBatchAsync(new[] { 1, 2, 3 });

        // 每个消息是 4 字节长度头 + 1 字节 JSON 数字。
        const int expectedLength = 3 * (LengthPrefixedFramer.HeaderSize + 1);

        var singleBytes = await ReadExactlyAsync(singlePipe.Reader, expectedLength);
        var batchBytes = await ReadExactlyAsync(batchPipe.Reader, expectedLength);

        Assert.Equal(singleBytes, batchBytes);
    }

    [Fact]
    public async Task WriteBatchAsync_EncodingFailure_AbortsConnection()
    {
        var pipe = new Pipe();
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, pipe.Writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new FailingCodec(2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WriteBatchAsync(new[] { 1, 2, 3 }).AsTask());

        Assert.Equal("encode-boom", error.Message);
        Assert.True(connection.Aborted);
        Assert.IsType<InvalidOperationException>(connection.AbortReason);
    }

    [Fact]
    public async Task WriteBatchAsync_OversizeMessage_AbortsConnection()
    {
        var pipe = new Pipe();
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, pipe.Writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new FixedSizeCodec(64),
            new MessageChannelOptions { MaxMessageSize = 16 });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => session.WriteBatchAsync(new[] { 1, 2 }).AsTask());

        Assert.True(connection.Aborted);
    }

    [Fact]
    public async Task WriteBatchAsync_FlushCanceled_AbortsConnection()
    {
        var pipe = new Pipe();
        var writer = new BlockingFlushPipeWriter(pipe.Writer);
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        using var cts = new CancellationTokenSource();
        var write = session.WriteBatchAsync(new[] { 1 }, cts.Token).AsTask();

        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(connection.Aborted);
    }

    [Fact]
    public async Task WriteBatchAsync_ConcurrentWithSingleWrite_DoesNotInterleave()
    {
        var pipe = new Pipe();
        var connection = new TestChannelConnection(new DuplexPipe(pipe.Reader, pipe.Writer));
        var context = new ChannelContext("batch", connection, EmptyServiceProvider.Instance);
        await using var session = context.AsSession(
            new LengthPrefixedFramer(),
            new JsonMessageCodec<int>());

        var batch = session.WriteBatchAsync(new[] { 1, 2, 3 }).AsTask();
        var single = session.WriteAsync(4, CancellationToken.None).AsTask();

        await Task.WhenAll(batch, single);

        var messages = await ReadIntsAsync(session, 4);

        Assert.Equal(new[] { 1, 2, 3, 4 }, messages);
    }

    [Fact]
    public async Task WriteBatchAsync_LoopbackLinesArriveInOrder()
    {
        var received = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            for (var i = 0; i < 3; i++)
                received.Add(await session.ReadAsync(context.ConnectionClosed));

            done.TrySetResult();
        });

        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = server.EndPoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });

        await client.ConnectAsync(CancellationToken.None);

        await using var session = client.Connection!.AsLineTextSession();
        await session.WriteBatchAsync(new[] { "alpha", "beta", "gamma" });

        await done.Task.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, received);
    }

    private static async Task<List<int>> ReadIntsAsync(IChannelSession<int> session, int count)
    {
        var messages = new List<int>(count);

        for (var i = 0; i < count; i++)
        {
            var message = await session.ReadAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TestWaits.DefaultTimeout);
            messages.Add(message);
        }

        return messages;
    }

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        var bytes = new List<byte>(count);

        while (bytes.Count < count)
        {
            var result = await reader.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);

            foreach (var segment in result.Buffer)
                bytes.AddRange(segment.ToArray());

            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
                break;
        }

        return bytes.ToArray();
    }
}
