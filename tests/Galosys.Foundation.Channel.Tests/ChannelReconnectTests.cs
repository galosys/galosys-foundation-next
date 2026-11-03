using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelReconnectTests
{
    [Fact]
    public async Task ConnectAsync_RetriesInitialConnection_WithFakeClock()
    {
        var endpoint = GetClosedLoopbackEndPoint();
        var time = new FakeTimeProvider();

        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = endpoint,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = time,
            Reconnect = CreatePolicy(maxAttempts: 3),
        });

        var connect = client.ConnectAsync(CancellationToken.None);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未进入 Reconnecting。");

        await using var server = await TestServer.StartAsync(
            TestHandlers.EchoAsync,
            options => options.EndPoint = endpoint);

        await AdvanceUntilAsync(
            time,
            () => Task.FromResult(connect.IsCompleted),
            TimeSpan.FromMilliseconds(100),
            "首次连接重试未在假时钟推进后完成。");

        await connect.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(ChannelConnectionState.Connected, client.State);
        Assert.NotNull(client.Connection);
    }

    [Fact]
    public async Task ConnectAsync_CallerCancellation_DuringBackoff_EndsDisconnected()
    {
        var endpoint = GetClosedLoopbackEndPoint();
        var time = new FakeTimeProvider();

        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = endpoint,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = time,
            Reconnect = new ChannelReconnectPolicy
            {
                MaxAttempts = 0,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
            },
        });

        using var cts = new CancellationTokenSource();
        var connect = client.ConnectAsync(cts.Token);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未进入 Reconnecting。");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Disconnected),
            "客户端未在调用方取消后回到 Disconnected。");
    }

    [Fact]
    public async Task ConnectionDropped_ReconnectsOnNextAttempt()
    {
        var connections = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                await release.Task;
                context.Abort(new IOException("drop-first"));
                return;
            }

            await TestHandlers.EchoAsync(context);
        });

        var time = new FakeTimeProvider();
        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = server.EndPoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            TimeProvider = time,
            Reconnect = CreatePolicy(maxAttempts: 3),
        });

        await client.ConnectAsync(CancellationToken.None);
        var firstConnection = client.Connection;
        Assert.NotNull(firstConnection);
        var firstId = firstConnection!.ConnectionId;

        release.TrySetResult();

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未在断线后进入 Reconnecting。");

        await AdvanceUntilAsync(
            time,
            () => Task.FromResult(client.State == ChannelConnectionState.Connected),
            TimeSpan.FromMilliseconds(100),
            "客户端未在退避后重连成功。");

        Assert.NotNull(client.Connection);
        Assert.NotEqual(firstId, client.Connection!.ConnectionId);
        Assert.True(Volatile.Read(ref connections) >= 2);
    }

    [Fact]
    public async Task RemoteEof_ReconnectsWhenPolicyConfigured()
    {
        var connections = 0;

        await using var server = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                context.Writer.Complete();
                return;
            }

            await TestHandlers.EchoAsync(context);
        });

        var time = new FakeTimeProvider();
        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = server.EndPoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            TimeProvider = time,
            Reconnect = CreatePolicy(maxAttempts: 3),
        });

        await client.ConnectAsync(CancellationToken.None);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未在对端 EOF 后进入 Reconnecting。");

        await AdvanceUntilAsync(
            time,
            () => Task.FromResult(client.State == ChannelConnectionState.Connected),
            TimeSpan.FromMilliseconds(100),
            "客户端未在对端 EOF 后重连成功。");

        Assert.True(Volatile.Read(ref connections) >= 2);
    }

    [Fact]
    public async Task MaxAttemptsExhausted_EndsDisconnected()
    {
        var endpoint = GetClosedLoopbackEndPoint();
        var time = new FakeTimeProvider();

        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = endpoint,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = time,
            Reconnect = new ChannelReconnectPolicy
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromMilliseconds(100),
                JitterFactor = 0,
            },
        });

        var connect = client.ConnectAsync(CancellationToken.None);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未进入 Reconnecting。");

        await AdvanceUntilAsync(
            time,
            () => Task.FromResult(connect.IsCompleted),
            TimeSpan.FromMilliseconds(100),
            "重试次数耗尽后连接操作未结束。");

        var error = await Record.ExceptionAsync(() => connect);
        Assert.NotNull(error);
        Assert.True(error is SocketException or TimeoutException);
        Assert.Equal(ChannelConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task CloseAsync_DuringReconnecting_StopsFurtherAttempts()
    {
        var connections = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                await release.Task;
                context.Abort(new IOException("drop"));
                return;
            }

            await TestHandlers.EchoAsync(context);
        });

        var time = new FakeTimeProvider();
        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = server.EndPoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            TimeProvider = time,
            Reconnect = new ChannelReconnectPolicy
            {
                MaxAttempts = 0,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
            },
        });

        await client.ConnectAsync(CancellationToken.None);
        release.TrySetResult();

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未进入 Reconnecting。");

        var before = Volatile.Read(ref connections);
        await client.CloseAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(ChannelConnectionState.Closed, client.State);
        Assert.Equal(before, Volatile.Read(ref connections));
    }

    [Fact]
    public async Task ShouldRetryFalse_StopsImmediately()
    {
        var endpoint = GetClosedLoopbackEndPoint();
        var time = new FakeTimeProvider();

        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = endpoint,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = time,
            Reconnect = new ChannelReconnectPolicy
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
                ShouldRetry = _ => false,
            },
        });

        var states = new List<ChannelConnectionState>();
        client.StateChanged += (_, args) => states.Add(args.NewState);

        var error = await Record.ExceptionAsync(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.NotNull(error);
        Assert.True(error is SocketException or TimeoutException);
        Assert.DoesNotContain(ChannelConnectionState.Reconnecting, states);
        Assert.Equal(ChannelConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task ConnectAsync_WithTls_DoesNotEnterReconnecting()
    {
        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1),
            ConnectTimeout = TimeSpan.FromSeconds(1),
            TimeProvider = new FakeTimeProvider(),
            Reconnect = CreatePolicy(maxAttempts: 3),
            Tls = new ChannelTlsOptions
            {
                ClientOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" },
            },
        });

        var states = new List<ChannelConnectionState>();
        client.StateChanged += (_, args) => states.Add(args.NewState);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.ConnectAsync(CancellationToken.None));

        Assert.DoesNotContain(ChannelConnectionState.Reconnecting, states);
        Assert.Equal(ChannelConnectionState.Disconnected, client.State);
    }

    private static ChannelReconnectPolicy CreatePolicy(int maxAttempts)
        => new()
        {
            MaxAttempts = maxAttempts,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(1),
            JitterFactor = 0,
        };

    private static IPEndPoint GetClosedLoopbackEndPoint()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var endpoint = (IPEndPoint)probe.LocalEndpoint;
        probe.Stop();
        return endpoint;
    }

    private static async Task AdvanceUntilAsync(
        FakeTimeProvider time,
        Func<Task<bool>> condition,
        TimeSpan step,
        string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message);

            time.Advance(step);
            await Task.Delay(10);
        }
    }
}
