using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelClientTests
{
    [Fact]
    public async Task ConnectAsync_EchoesBytesAndPublishesStates()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        var states = new List<(ChannelConnectionState Old, ChannelConnectionState New)>();
        client.StateChanged += (_, args) => states.Add((args.OldState, args.NewState));

        await client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(ChannelConnectionState.Connected, client.State);
        Assert.NotNull(client.Connection);
        Assert.Equal(
            new[]
            {
                (ChannelConnectionState.Disconnected, ChannelConnectionState.Connecting),
                (ChannelConnectionState.Connecting, ChannelConnectionState.Connected),
            },
            states);

        await client.Connection!.Writer.WriteAsync("ping"u8.ToArray());
        var payload = await ReadExactlyAsync(client.Connection.Reader, 4);
        Assert.Equal("ping", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public async Task ConnectAsync_ConcurrentCalls_ShareOneConnection()
    {
        var accepted = 0;
        await using var server = await TestServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref accepted);
            await TestHandlers.EchoAsync(context);
        });

        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        var first = client.ConnectAsync(CancellationToken.None);
        var second = client.ConnectAsync(CancellationToken.None);
        await Task.WhenAll(first, second).WaitAsync(TestWaits.DefaultTimeout);

        var connection = client.Connection;
        Assert.NotNull(connection);
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(Volatile.Read(ref accepted) == 1),
            "服务端未接受客户端连接。");
        await client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout);

        Assert.Same(connection, client.Connection);
        Assert.Equal(1, Volatile.Read(ref accepted));
    }

    [Fact]
    public async Task ConnectAsync_WhenRefused_PublishesRetryableFailure()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var endpoint = (IPEndPoint)probe.LocalEndpoint;
        probe.Stop();

        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = endpoint,
                // 本机对 loopback 关闭端口的拒绝可能延迟约 2 秒，超时必须留出余量。
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            .Build();

        ChannelConnectionFailure? failure = null;
        client.StateChanged += (_, args) =>
        {
            if (args.NewState == ChannelConnectionState.Disconnected)
                failure = args.Failure;
        };

        await Assert.ThrowsAnyAsync<SocketException>(
            () => client.ConnectAsync(CancellationToken.None).WaitAsync(TestWaits.DefaultTimeout));

        Assert.Equal(ChannelConnectionState.Disconnected, client.State);
        Assert.NotNull(failure);
        Assert.Equal(ChannelConnectionFailureKind.ConnectionRefused, failure!.Kind);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public async Task ConnectAsync_WithTls_ThrowsNotSupportedWithoutSocket()
    {
        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1),
                ConnectTimeout = TimeSpan.FromSeconds(1),
                Tls = new ChannelTlsOptions
                {
                    ClientOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" },
                },
            })
            .Build();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.Equal(ChannelConnectionState.Disconnected, client.State);
        Assert.Null(client.Connection);
    }

    [Fact]
    public async Task CloseAsync_DrainsOutput_AndIsTerminal()
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            var buffer = new byte[3];
            var offset = 0;

            while (offset < buffer.Length)
            {
                var result = await context.Reader.ReadAsync();
                if (result.IsCompleted && result.Buffer.IsEmpty)
                    break;

                var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, buffer.Length - offset));
                slice.CopyTo(buffer.AsSpan(offset));
                offset += (int)slice.Length;
                context.Reader.AdvanceTo(slice.End);
            }

            received.TrySetResult(buffer);
        });

        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        await client.ConnectAsync(CancellationToken.None);
        await client.Connection!.Writer.WriteAsync("bye"u8.ToArray());
        await client.CloseAsync(CancellationToken.None);

        Assert.Equal(ChannelConnectionState.Closed, client.State);
        Assert.Null(client.Connection);
        Assert.Equal("bye", Encoding.UTF8.GetString(await received.Task.WaitAsync(TestWaits.DefaultTimeout)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndInvalidatesOldConnection()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        await client.ConnectAsync(CancellationToken.None);
        var oldConnection = client.Connection!;

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(ChannelConnectionState.Closed, client.State);
        Assert.True(oldConnection.ConnectionClosed.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await oldConnection.Writer.WriteAsync("x"u8.ToArray()));
    }

    [Fact]
    public async Task ConnectionLost_TransitionsToDisconnected()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            await release.Task;
            context.Abort(new IOException("server-drop"));
        });

        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        try
        {
            await client.ConnectAsync(CancellationToken.None);
            var connection = client.Connection;
            Assert.NotNull(connection);

            release.TrySetResult();

            await TestClient.WaitUntilAsync(
                () => Task.FromResult(client.State == ChannelConnectionState.Disconnected),
                "客户端未在远端断开后迁移到 Disconnected。");

            Assert.Null(client.Connection);
            Assert.True(connection.ConnectionClosed.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task RemoteEof_TransitionsToDisconnected()
    {
        await using var server = await TestServer.StartAsync(context =>
        {
            context.Writer.Complete();
            return Task.CompletedTask;
        });

        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        await client.ConnectAsync(CancellationToken.None);
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Disconnected),
            "客户端未在对端 EOF 后迁移到 Disconnected。");

        Assert.Null(client.Connection);
    }

    [Fact]
    public async Task CloseAsync_IsIdempotent_AndDoesNotRaiseClosedTwice()
    {
        await using var server = await TestServer.StartAsync(TestHandlers.EchoAsync);
        await using var client = ChannelClientBuilder
            .Create(new ChannelClientOptions
            {
                EndPoint = server.EndPoint,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            })
            .Build();

        await client.ConnectAsync(CancellationToken.None);

        var closedEvents = 0;
        client.StateChanged += (_, args) =>
        {
            if (args.NewState == ChannelConnectionState.Closed)
                Interlocked.Increment(ref closedEvents);
        };

        await client.CloseAsync(CancellationToken.None);
        await client.CloseAsync(CancellationToken.None);

        Assert.Equal(ChannelConnectionState.Closed, client.State);
        Assert.Equal(1, Volatile.Read(ref closedEvents));
    }

    private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var result = await reader.ReadAsync().AsTask().WaitAsync(TestWaits.DefaultTimeout);
            var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, count - offset));
            slice.CopyTo(buffer.AsSpan(offset));
            offset += (int)slice.Length;
            reader.AdvanceTo(slice.End);

            if (result.IsCompleted && offset < count)
                throw new IOException("连接在读取完成前关闭。");
        }

        return buffer;
    }
}
