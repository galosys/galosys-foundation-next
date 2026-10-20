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
    public async Task MaxConnections_RejectsExcessConnections()
    {
        var release = new CancellationTokenSource();
        await using var handle = await TestServer.StartAsync(
            context => TestHandlers.HoldAsync(context, release.Token),
            options => options.MaxConnections = 1);

        using var first = await TestClient.ConnectAsync(handle.EndPoint);
        await Task.Delay(50);

        using var second = await TestClient.ConnectAsync(handle.EndPoint);
        var read = await TestClient.ReadOneByteAsync(second.GetStream());

        Assert.Equal(0, read);

        release.Cancel();
    }
}
