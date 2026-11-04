using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

/// <summary>
/// README 代码片段的可编译性回归测试。
/// </summary>
/// <remarks>
/// README 里的示例是使用者唯一能照抄的入口，一旦与真实 API 脱节就会静默误导人。
/// 本文件把 README 中的关键片段原样搬成测试，README 改了这里就会编译失败。
/// </remarks>
public sealed class ReadmeSnippetCompileTests
{
    /// <summary>「最小用法」章节的服务端片段。</summary>
    [Fact]
    public async Task ServerSnippet_Compiles()
    {
        var server = ChannelServerBuilder
            .Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
            })
            .Run(TestHandlers.EchoAsync)
            .Build();

        await server.StartAsync(CancellationToken.None);
        await server.StopAsync(CancellationToken.None);
        await server.DisposeAsync();
    }

    /// <summary>「服务端 TLS」章节的片段。</summary>
    [Fact]
    public async Task TlsSnippet_Compiles()
    {
        var tls = new ChannelTlsOptions
        {
            ServerOptions = new System.Net.Security.SslServerAuthenticationOptions
            {
                ServerCertificate = TlsTestCertificates.CreateSelfSigned(),
            },
        };

        var server = ChannelServerBuilder
            .Create(options => options.EndPoint = new IPEndPoint(IPAddress.Any, 8000))
            .UseTls(tls)
            .Use(async (context, next) => await next(context))
            .Run(TestHandlers.EchoAsync)
            .Build();

        await server.DisposeAsync();
    }

    /// <summary>「单连接客户端」章节的片段。</summary>
    [Fact]
    public async Task ClientSnippet_Compiles()
    {
        await using var client = ChannelClientBuilder
            .Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
                options.ConnectTimeout = TimeSpan.FromSeconds(5);
            })
            .Build();

        Assert.NotNull(client);
    }

    /// <summary>「重连与退避」章节的片段。</summary>
    [Fact]
    public async Task ReconnectSnippet_Compiles()
    {
        var endPoint = new IPEndPoint(IPAddress.Loopback, 8000);

        var client = ChannelClientBuilder
            .Create(options =>
            {
                options.EndPoint = endPoint;
                options.Reconnect = new ChannelReconnectPolicy
                {
                    MaxAttempts = 0,
                    InitialDelay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(30),
                    BackoffMultiplier = 2.0,
                    JitterFactor = 0.2,
                };
            })
            .Build();

        await client.DisposeAsync();
    }

    /// <summary>「连接池」章节的片段。</summary>
    [Fact]
    public async Task PoolSnippet_Compiles()
    {
        await using var pool = ChannelClientBuilder
            .Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
                options.ConnectTimeout = TimeSpan.FromSeconds(5);
            })
            .AsPool()
            .WithMaxSize(8)
            .WithRentTimeout(TimeSpan.FromSeconds(30))
            .Build();

        Assert.NotNull(pool);
    }

    /// <summary>「多服务器组合」章节的片段。</summary>
    [Fact]
    public async Task GroupSnippet_Compiles()
    {
        var http = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080))
            .Run(TestHandlers.EchoAsync)
            .Build();

        var mqtt = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 1883))
            .Run(TestHandlers.EchoAsync)
            .Build();

        await using var group = new ChannelServerGroup(http, mqtt);
        await group.StartAsync(CancellationToken.None);
        await group.StopAsync(CancellationToken.None);
    }

    /// <summary>「协议分发」章节的片段。</summary>
    [Fact]
    public async Task ProtocolDispatchSnippet_Compiles()
    {
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("HTTP/1."u8.ToArray(), new EchoProtocol()),
        });

        var dispatcher = new ProtocolDispatcher(selector);

        var server = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080))
            .Run(dispatcher.HandleAsync)
            .Build();

        await server.DisposeAsync();
    }

    /// <summary>「同端口前缀路由」章节的片段。</summary>
    [Fact]
    public async Task PrefixRoutingSnippet_Compiles()
    {
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("HTTP/1."u8.ToArray(), new EchoProtocol()),
        });

        var server = ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8080))
            .Run(new ProtocolDispatcher(selector).HandleAsync)
            .Build();

        await server.DisposeAsync();
    }

    /// <summary>客户端构建器的其余链式配置方法。</summary>
    [Fact]
    public async Task ClientBuilderOptions_Compile()
    {
        await using var client = ChannelClientBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000))
            .WithReconnect(new ChannelReconnectPolicy { MaxAttempts = 3 })
            .WithTimeProvider(TimeProvider.System)
            .WithKeepAlive(new ChannelKeepAliveOptions { Enabled = true })
            .Build();

        Assert.NotNull(client);
    }

    /// <summary>最小协议实现，仅用于让协议相关片段能编译。</summary>
    private sealed class EchoProtocol : IChannelProtocol
    {
        public string Name => "echo";

        public ValueTask HandleAsync(ChannelContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
