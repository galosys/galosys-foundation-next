using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task LineSession_HalfPackets_AreReassembled()
    {
        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            var line = await session.ReadAsync(context.ConnectionClosed);
            await session.WriteAsync(line, context.ConnectionClosed);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync("hel"u8.ToArray());
        await Task.Delay(50);
        await stream.WriteAsync("lo\n"u8.ToArray());

        Assert.Equal("hello", await ReadLineAsync(stream));
    }

    [Fact]
    public async Task LineSession_StickyPackets_AreSplitIntoMessages()
    {
        await using var server = await StartLineEchoAsync();
        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync("a\nb\n"u8.ToArray());

        Assert.Equal("a", await ReadLineAsync(stream));
        Assert.Equal("b", await ReadLineAsync(stream));
    }

    [Fact]
    public async Task LineSession_CleanEof_ThrowsEndOfStream()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            try
            {
                await session.ReadAsync(context.ConnectionClosed);
            }
            catch (Exception ex)
            {
                observed.TrySetResult(ex);
            }
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.IsType<EndOfStreamException>(await observed.Task.WaitAsync(TestWaits.DefaultTimeout));
    }

    [Fact]
    public async Task LineSession_MidFrameEof_ThrowsInvalidData()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            try
            {
                await session.ReadAsync(context.ConnectionClosed);
            }
            catch (Exception ex)
            {
                observed.TrySetResult(ex);
            }
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        await client.GetStream().WriteAsync("partial"u8.ToArray());
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.IsType<InvalidDataException>(await observed.Task.WaitAsync(TestWaits.DefaultTimeout));
    }

    [Fact]
    public async Task LineSession_ExceedsMaxMessageSize_ThrowsInvalidData()
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession(
                options: new MessageChannelOptions { MaxMessageSize = 4 });
            try
            {
                await session.ReadAsync(context.ConnectionClosed);
            }
            catch (Exception ex)
            {
                observed.TrySetResult(ex);
            }
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        await client.GetStream().WriteAsync("12345\n"u8.ToArray());

        Assert.IsType<InvalidDataException>(await observed.Task.WaitAsync(TestWaits.DefaultTimeout));
    }

    [Fact]
    public async Task LineSession_ReadCancellation_DoesNotConsume()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();

            using var cts = new CancellationTokenSource();
            var first = session.ReadAsync(cts.Token).AsTask();
            cts.Cancel();

            try
            {
                await first;
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
            }

            var line = await session.ReadAsync(context.ConnectionClosed);
            await session.WriteAsync(line, context.ConnectionClosed);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        await canceled.Task.WaitAsync(TestWaits.DefaultTimeout);
        await stream.WriteAsync("ok\n"u8.ToArray());

        Assert.Equal("ok", await ReadLineAsync(stream));
    }

    [Fact]
    public async Task LineSession_ConcurrentRead_Throws()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFailed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();

            var first = session.ReadAsync(context.ConnectionClosed).AsTask();
            firstStarted.TrySetResult();

            try
            {
                await session.ReadAsync(context.ConnectionClosed);
            }
            catch (Exception ex)
            {
                secondFailed.TrySetResult(ex);
            }

            var line = await first;
            await session.WriteAsync(line, context.ConnectionClosed);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        await firstStarted.Task.WaitAsync(TestWaits.DefaultTimeout);
        Assert.IsType<InvalidOperationException>(
            await secondFailed.Task.WaitAsync(TestWaits.DefaultTimeout));

        await stream.WriteAsync("go\n"u8.ToArray());
        Assert.Equal("go", await ReadLineAsync(stream));
    }

    [Fact]
    public async Task LineSession_ConcurrentWrites_AreSerialized()
    {
        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            var first = session.WriteAsync("first", context.ConnectionClosed).AsTask();
            var second = session.WriteAsync("second", context.ConnectionClosed).AsTask();
            await Task.WhenAll(first, second);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        Assert.Equal("first", await ReadLineAsync(stream));
        Assert.Equal("second", await ReadLineAsync(stream));
    }

    [Fact]
    public async Task LineSession_OneMiB_Backpressure()
    {
        await using var server = await StartLineEchoAsync();
        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        var payload = new string('a', 1024 * 1024);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n"));

        Assert.Equal(payload, await ReadLineAsync(stream));
    }

    [Fact]
    public async Task DuplicateAsSession_Throws_AndDisposeDoesNotCloseConnection()
    {
        var duplicate = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedAfterDispose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            var session = context.AsLineTextSession();

            try
            {
                _ = context.AsLineTextSession();
            }
            catch (Exception ex)
            {
                duplicate.TrySetResult(ex);
            }

            await session.DisposeAsync();
            closedAfterDispose.TrySetResult(context.ConnectionClosed.IsCancellationRequested);

            await context.Writer.WriteAsync("open"u8.ToArray());
            await context.Writer.FlushAsync();
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);

        Assert.IsType<InvalidOperationException>(
            await duplicate.Task.WaitAsync(TestWaits.DefaultTimeout));
        Assert.False(await closedAfterDispose.Task.WaitAsync(TestWaits.DefaultTimeout));

        var received = await TestClient.ReadExactlyAsync(client.GetStream(), 4);
        Assert.Equal("open", Encoding.UTF8.GetString(received));
    }

    [Fact]
    public async Task JsonSession_RoundTrip()
    {
        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsJsonSession<TestMessage>();
            var message = await session.ReadAsync(context.ConnectionClosed);
            await session.WriteAsync(message, context.ConnectionClosed);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();

        await WriteLengthPrefixedAsync(
            stream,
            JsonSerializer.SerializeToUtf8Bytes(new TestMessage(1, "hello")));

        var response = await ReadLengthPrefixedAsync(stream);
        var decoded = JsonSerializer.Deserialize<TestMessage>(response);

        Assert.Equal(new TestMessage(1, "hello"), decoded);
    }

    [Fact]
    public async Task LineTextSession_MultibyteUtf8SplitAcrossWrites_DecodesWholeCharacter()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession();
            var line = await session.ReadAsync(context.ConnectionClosed);
            received.TrySetResult(line);
        });

        using var client = await TestClient.ConnectAsync(server.EndPoint);
        var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes("你好\n");

        await stream.WriteAsync(bytes.AsMemory(0, 1));
        await Task.Delay(50);
        await stream.WriteAsync(bytes.AsMemory(1));

        Assert.Equal("你好", await received.Task.WaitAsync(TestWaits.DefaultTimeout));
    }

    private static async Task<TestServerHandle> StartLineEchoAsync(MessageChannelOptions? options = null)
    {
        return await TestServer.StartAsync(async context =>
        {
            await using var session = context.AsLineTextSession(options: options);
            while (true)
            {
                string line;
                try
                {
                    line = await session.ReadAsync(context.ConnectionClosed);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                await session.WriteAsync(line, context.ConnectionClosed);
            }
        });
    }

    private static async Task WriteLengthPrefixedAsync(NetworkStream stream, byte[] payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(NetworkStream stream)
    {
        var header = await TestClient.ReadExactlyAsync(stream, 4);
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
        return await TestClient.ReadExactlyAsync(stream, length);
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one).AsTask().WaitAsync(TestWaits.DefaultTimeout);
            if (read == 0)
                throw new IOException("连接在读到换行前关闭。");

            if (one[0] == (byte)'\n')
                break;

            buffer.WriteByte(one[0]);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
