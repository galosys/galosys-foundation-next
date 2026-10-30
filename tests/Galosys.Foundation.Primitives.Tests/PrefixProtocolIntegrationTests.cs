using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PrefixProtocolIntegrationTests
{
    [Fact]
    public async Task SamePort_TwoPrefixes_DispatchToDistinctHandlers()
    {
        var alpha = new PrefixEchoProtocol("alpha", new byte[] { 0x01 }, "alpha");
        var beta = new PrefixEchoProtocol("beta", new byte[] { 0x02 }, "beta");
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule(alpha.Prefix, alpha),
            new PrefixProtocolRule(beta.Prefix, beta),
        });
        var dispatcher = new ProtocolDispatcher(selector);

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(dispatcher.HandleAsync));

        await server.StartAsync(CancellationToken.None);

        try
        {
            var endPoint = (IPEndPoint)server.LocalEndPoint!;

            using (var client = await TestClient.ConnectAsync(endPoint))
            {
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 0x01, (byte)'x' });
                var response = await TestClient.ReadExactlyAsync(
                    stream,
                    alpha.Response.Length);

                Assert.Equal("alpha", Encoding.UTF8.GetString(response));
            }

            using (var client = await TestClient.ConnectAsync(endPoint))
            {
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 0x02, (byte)'y' });
                var response = await TestClient.ReadExactlyAsync(
                    stream,
                    beta.Response.Length);

                Assert.Equal("beta", Encoding.UTF8.GetString(response));
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task SamePort_PartialPrefix_WaitsForRemainingBytes()
    {
        var protocol = new PrefixEchoProtocol(
            "partial",
            new byte[] { 0x10, 0x20 },
            "partial");

        await using var server = StartServer(protocol);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var endPoint = (IPEndPoint)server.LocalEndPoint!;
            using var client = await TestClient.ConnectAsync(endPoint);
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x10 });
            await stream.FlushAsync();
            await Task.Delay(100);
            await stream.WriteAsync(new byte[] { 0x20, (byte)'x' });
            await stream.FlushAsync();

            var response = await TestClient.ReadExactlyAsync(
                stream,
                protocol.Response.Length);

            Assert.Equal("partial", Encoding.UTF8.GetString(response));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task SamePort_UnknownPrefix_AbortsConnection_AndServerKeepsAccepting()
    {
        var alpha = new PrefixEchoProtocol("alpha", new byte[] { 0x01 }, "alpha");

        await using var server = StartServer(alpha);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var endPoint = (IPEndPoint)server.LocalEndPoint!;

            using (var unknown = await TestClient.ConnectAsync(endPoint))
            {
                var stream = unknown.GetStream();
                await stream.WriteAsync(new byte[] { 0xFF });
                await stream.FlushAsync();
                await TestClient.AssertConnectionClosedAsync(stream);
            }

            using (var valid = await TestClient.ConnectAsync(endPoint))
            {
                var stream = valid.GetStream();
                await stream.WriteAsync(new byte[] { 0x01, (byte)'x' });
                var response = await TestClient.ReadExactlyAsync(
                    stream,
                    alpha.Response.Length);

                Assert.Equal("alpha", Encoding.UTF8.GetString(response));
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static ChannelServer StartServer(params PrefixEchoProtocol[] protocols)
    {
        var rules = protocols
            .Select(protocol => new PrefixProtocolRule(protocol.Prefix, protocol))
            .ToArray();
        var dispatcher = new ProtocolDispatcher(new PrefixProtocolSelector(rules));

        return new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(dispatcher.HandleAsync));
    }

    private sealed class PrefixEchoProtocol : IChannelProtocol
    {
        private readonly string _response;

        public PrefixEchoProtocol(string name, byte[] prefix, string response)
        {
            Name = name;
            Prefix = prefix;
            _response = response;
        }

        public string Name { get; }

        public ReadOnlyMemory<byte> Prefix { get; }

        public string Response => _response;

        public async ValueTask HandleAsync(
            ChannelContext context,
            CancellationToken cancellationToken)
        {
            var read = await context.Reader.ReadAsync(cancellationToken);
            var buffer = read.Buffer;

            if (buffer.Length < Prefix.Length)
                return;

            context.Reader.AdvanceTo(buffer.GetPosition(Prefix.Length));

            await context.Writer.WriteAsync(
                Encoding.UTF8.GetBytes(_response),
                cancellationToken);
            await context.Writer.FlushAsync(cancellationToken);
        }
    }
}
