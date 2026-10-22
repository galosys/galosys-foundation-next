using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Echo_ReturnsSameBytes()
    {
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync("hello"u8.ToArray());
        var echoed = await TestClient.ReadExactlyAsync(stream, 5);

        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(echoed));
    }

    [Fact]
    public async Task Echo_HandlesOneMegabyteInBothDirections()
    {
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);

        var readTask = TestClient.ReadExactlyAsync(stream, payload.Length);
        await stream.WriteAsync(payload);
        var echoed = await readTask;

        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Framework_CompletesTransport_WhenHandlerReturns()
    {
        await using var handle = await TestServer.StartAsync(_ => Task.CompletedTask);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var read = await TestClient.ReadOneByteAsync(stream);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task GracefulClose_DrainsQueuedOutput()
    {
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        await using var handle = await TestServer.StartAsync(async context =>
        {
            await context.Writer.WriteAsync(payload);
        });

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        var received = await TestClient.ReadExactlyAsync(stream, payload.Length);

        Assert.Equal(payload, received);
        Assert.Equal(0, await TestClient.ReadOneByteAsync(stream));
    }

    [Fact]
    public async Task GracefulClose_WithUnreadInboundData_StillDeliversResponseAndFin()
    {
        var request = new byte[32 * 1024];
        var response = new byte[16 * 1024];
        Random.Shared.NextBytes(request);
        Random.Shared.NextBytes(response);

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                // 只消费一小部分入站数据，其余留在内核接收缓冲区，模拟慢/不读请求的处理器。
                var result = await context.Reader.ReadAsync();
                context.Reader.AdvanceTo(result.Buffer.End);
                await context.Writer.WriteAsync(response);
            },
            options =>
            {
                options.MaxReadBufferSize = 1024;
                options.MaxWriteBufferSize = 1024;
            });

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync(request).AsTask().WaitAsync(TestWaits.DefaultTimeout);

        var received = await TestClient.ReadExactlyAsync(stream, response.Length);
        Assert.Equal(response, received);

        // 优雅关闭必须以 FIN 结束；带未读入站数据直接关闭会变成 RST。
        Assert.Equal(0, await TestClient.ReadOneByteAsync(stream));
    }

    [Fact]
    public async Task ClientHalfClose_EchoesThenCloses()
    {
        await using var handle = await TestServer.StartAsync(TestHandlers.EchoAsync);
        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();

        await stream.WriteAsync("half"u8.ToArray());
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.Equal(
            "half",
            System.Text.Encoding.UTF8.GetString(await TestClient.ReadExactlyAsync(stream, 4)));
        Assert.Equal(0, await TestClient.ReadOneByteAsync(stream));
    }

    [Fact]
    public async Task TinyPipeThresholds_StillEcho()
    {
        await using var handle = await TestServer.StartAsync(
            TestHandlers.EchoAsync,
            options =>
            {
                options.MaxReadBufferSize = 1;
                options.MaxWriteBufferSize = 1;
            });

        using var client = await TestClient.ConnectAsync(handle.EndPoint);
        var stream = client.GetStream();
        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        var readTask = TestClient.ReadExactlyAsync(stream, payload.Length);
        await stream.WriteAsync(payload).AsTask().WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(payload, await readTask);
    }

    [Fact]
    public async Task MaxConnections_RejectsExcessConnections()
    {
        var release = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await TestServer.StartAsync(
            async context =>
            {
                accepted.TrySetResult();
                await TestHandlers.HoldAsync(context, release.Token);
            },
            options => options.MaxConnections = 1);

        using var first = await TestClient.ConnectAsync(handle.EndPoint);
        await accepted.Task.WaitAsync(TestWaits.DefaultTimeout);

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        await TestClient.AssertConnectionClosedAsync(second.GetStream());

        release.Cancel();
    }

    [Fact]
    public async Task MaxConnections_RejectsExcess_ButFirstConnectionStillEchoes()
    {
        await using var handle = await TestServer.StartAsync(
            TestHandlers.EchoAsync,
            options => options.MaxConnections = 1);

        using var first = await TestClient.ConnectAsync(handle.EndPoint);
        var firstStream = first.GetStream();
        await firstStream.WriteAsync("a"u8.ToArray());
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString(
            await TestClient.ReadExactlyAsync(firstStream, 1)));

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        await TestClient.AssertConnectionClosedAsync(second.GetStream());

        await firstStream.WriteAsync("b"u8.ToArray());
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString(
            await TestClient.ReadExactlyAsync(firstStream, 1)));
    }
}
