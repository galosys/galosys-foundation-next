using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

public sealed class ChannelServerBuilderTests
{
    [Fact]
    public async Task Build_ProducesServerThatEchoes()
    {
        await using var server = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .Run(TestHandlers.EchoAsync)
            .Build();

        await server.StartAsync(CancellationToken.None);

        try
        {
            Assert.NotNull(server.LocalEndPoint);

            using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());

            var echoed = await TestClient.ReadExactlyAsync(stream, 4);
            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Build_WithoutRun_Throws()
    {
        var builder = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Run_Twice_Throws()
    {
        var builder = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        builder.Run(TestHandlers.EchoAsync);

        Assert.Throws<InvalidOperationException>(() => builder.Run(TestHandlers.EchoAsync));
    }

    [Fact]
    public void Build_Twice_ProducesIndependentInstances()
    {
        var builder = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .Run(TestHandlers.EchoAsync);

        var first = builder.Build();
        var second = builder.Build();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Create_WithInvalidOptions_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ChannelServerBuilder.Create(new ChannelServerOptions()));
    }

    [Fact]
    public void UseTls_WithMissingCertificate_ThrowsImmediately()
    {
        var builder = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        Assert.Throws<ArgumentException>(() =>
            builder.UseTls(new ChannelTlsOptions
            {
                ServerOptions = new System.Net.Security.SslServerAuthenticationOptions(),
            }));
    }

    [Fact]
    public async Task Use_MiddlewaresRunInRegistrationOrder()
    {
        var order = new List<string>();

        await using var server = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .Use(async (context, next) =>
            {
                order.Add("first-in");
                await next(context);
                order.Add("first-out");
            })
            .Use(async (context, next) =>
            {
                order.Add("second-in");
                await next(context);
                order.Add("second-out");
            })
            .Run(async context =>
            {
                order.Add("handler");

                // 短暂阻塞，避免连接在断言前就结束。
                await Task.Delay(200);
            })
            .Build();

        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);

            // 中间件的 "out" 记录在 handler 返回之后才写入，因此要等完整序列出现。
            await TestClient.WaitUntilAsync(
                () => Task.FromResult(order.Count >= 5),
                "中间件执行顺序未被完整记录。");

            Assert.Equal(
                new[] { "first-in", "second-in", "handler", "second-out", "first-out" },
                order);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Build_SnapshotsOptions()
    {
        var options = new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        };

        var builder = ChannelServerBuilder.Create(options).Run(TestHandlers.EchoAsync);
        var server = builder.Build();

        await using (server)
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 12345);
            await server.StartAsync(CancellationToken.None);

            Assert.NotNull(server.LocalEndPoint);
            Assert.NotEqual(12345, ((IPEndPoint)server.LocalEndPoint!).Port);

            await server.StopAsync(CancellationToken.None);
        }
    }
}
