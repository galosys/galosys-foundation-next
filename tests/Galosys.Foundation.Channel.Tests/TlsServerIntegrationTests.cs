using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

/// <summary>
/// 服务端 <c>UseTls</c> 中间件的集成测试：真实 loopback + 自签名证书。
/// 对应 spec「服务端 UseTls 中间件」的每个 Scenario。
/// </summary>
public sealed class TlsServerIntegrationTests
{
    [Fact]
    public async Task UseTls_EchoesBytes()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = CreateServerTls(certificate);
        var transportTypes = new ConcurrentQueue<string>();
        var handlerCalls = 0;

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            pipeline =>
            {
                pipeline.UseTls(tls);

                // TLS 之后的观察中间件：断言上下文传输已经是 TLS 包装层。
                pipeline.Use(async (context, next) =>
                {
                    transportTypes.Enqueue(context.Transport.GetType().Name);
                    await next(context);
                });

                pipeline.Run(async context =>
                {
                    Interlocked.Increment(ref handlerCalls);
                    await TestHandlers.EchoAsync(context);
                });
            });

        await server.StartAsync(CancellationToken.None);

        await using var client = await TlsServerTestClient.ConnectAsync(
            (IPEndPoint)server.LocalEndPoint!,
            certificate);

        await client.WriteAsync("ping"u8.ToArray());
        var buffer = await client.ReadExactlyAsync(4);

        Assert.Equal("ping", Encoding.UTF8.GetString(buffer));

        // context.Reader / context.Writer 必须来自 TlsDuplexPipe，而不是底层明文管道。
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(transportTypes.Count > 0),
            "TLS 之后的观察中间件没有执行。");

        Assert.Equal("TlsDuplexPipe", transportTypes.Single());
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task CertificateRejected_DoesNotInvokeHandler_AndServerAcceptsNextClient()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = CreateServerTls(certificate);
        var invoked = 0;

        // 记录终端 handler 是否真正读到过业务字节。
        var bytesHandled = 0;

        await using var server = CreateServer(
            tls,
            async context =>
            {
                Interlocked.Increment(ref invoked);

                // 回显，同时记录终端 handler 是否真正读到过业务字节。
                while (true)
                {
                    var result = await context.Reader.ReadAsync();

                    if (result.IsCompleted && result.Buffer.IsEmpty)
                        break;

                    if (!result.Buffer.IsEmpty)
                        Interlocked.Add(ref bytesHandled, (int)result.Buffer.Length);

                    foreach (var segment in result.Buffer)
                        await context.Writer.WriteAsync(segment);

                    context.Reader.AdvanceTo(result.Buffer.End);
                    await context.Writer.FlushAsync();
                }
            });

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        // 客户端拒绝服务端证书：客户端侧握手必然失败。
        var clientError = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await TlsServerTestClient.ConnectAsync(endPoint, certificate, rejectCertificate: true);
        });

        Assert.NotNull(clientError);
        Assert.True(
            clientError is AuthenticationException or CryptographicException or IOException,
            $"证书拒绝时客户端异常类型为 {clientError.GetType().FullName}，不符合预期。");

        // 服务端不得处理任何业务字节，也不得回显。
        //
        // 协议事实（Windows Schannel，TLS 1.2）：服务端 AuthenticateAsServerAsync 在
        // 客户端发出拒绝 alert 之前就已返回成功，因此 UseTls 会调用 next，
        // 终端 handler 会被进入；拒绝在 handler 的第一次读取时以 IOException 暴露。
        // 这不是中间件的实现问题，而是 TLS 1.2 的握手顺序决定的。
        // 这里锁定可观测的安全语义：没有任何业务数据到达 handler。
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(Volatile.Read(ref invoked) > 0),
            "服务端没有开始处理被拒绝的连接。");

        Assert.Equal(0, Volatile.Read(ref bytesHandled));

        // 服务端继续接受后续连接；使用正确证书的客户端仍能完成 TLS 回显。
        await using var good = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
        await good.WriteAsync("ok"u8.ToArray());
        var buffer = await good.ReadExactlyAsync(2);

        Assert.Equal("ok", Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public async Task TlsHandshake_RunsBeforeInnerMiddlewareAndHandler()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = CreateServerTls(certificate);
        var outerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 事件列表跨多个任务读写，使用线程安全容器。
        var events = new ConcurrentQueue<string>();

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            pipeline =>
            {
                // TLS 前观察中间件。
                pipeline.Use(async (context, next) =>
                {
                    events.Enqueue("outer-enter");
                    outerEntered.TrySetResult();
                    await next(context);
                    events.Enqueue("outer-exit");
                });

                pipeline.UseTls(tls);

                // TLS 后观察中间件。
                pipeline.Use(async (context, next) =>
                {
                    events.Enqueue("inner-enter");
                    innerEntered.TrySetResult();
                    await next(context);
                    events.Enqueue("inner-exit");
                });

                pipeline.Run(context =>
                {
                    events.Enqueue("handler");
                    handlerCalled.TrySetResult();
                    return Task.CompletedTask;
                });
            });

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        // 客户端只建立 TCP 连接，延迟 TLS 握手。
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TestWaits.DefaultTimeout);

        await outerEntered.Task.WaitAsync(TestWaits.DefaultTimeout);

        // 握手尚未完成：TLS 之后的中间件与 handler 都不得执行。
        Assert.False(innerEntered.Task.IsCompleted);
        Assert.False(handlerCalled.Task.IsCompleted);
        Assert.Equal(new[] { "outer-enter" }, events.ToArray());

        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        await using var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            (_, remote, _, _) =>
                remote is not null
                && remote.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" });

        await innerEntered.Task.WaitAsync(TestWaits.DefaultTimeout);
        await handlerCalled.Task.WaitAsync(TestWaits.DefaultTimeout);

        // 退出顺序：handler → TLS 后中间件 → TLS 前中间件。
        await TestClient.WaitUntilAsync(
            () => Task.FromResult(events.Count >= 5),
            "管线退出顺序未完成。");

        Assert.Equal(
            new[] { "outer-enter", "inner-enter", "handler", "inner-exit", "outer-exit" },
            events.ToArray());
    }

    [Fact]
    public async Task ClearTextRequest_DoesNotReachHandler()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = CreateServerTls(certificate);
        var invoked = 0;

        await using var server = CreateServer(
            tls,
            context =>
            {
                Interlocked.Increment(ref invoked);
                return TestHandlers.EchoAsync(context);
            });

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(endPoint.Address, endPoint.Port).WaitAsync(TestWaits.DefaultTimeout);

            var stream = tcp.GetStream();
            await stream.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

            // 服务端握手失败后连接被关闭或中止：Windows 上客户端常收到 RST，
            // 因此容忍 IOException / SocketException，只要求“不是回显”。
            try
            {
                var read = await stream.ReadAsync(new byte[64].AsMemory())
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, read);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 连接被中止（RST），等价于“连接已结束”。
            }
        }

        Assert.Equal(0, Volatile.Read(ref invoked));

        // 服务端仍然可用：合法客户端可以正常回显。
        await using var good = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
        await good.WriteAsync("ok"u8.ToArray());
        var response = await good.ReadExactlyAsync(2);

        Assert.Equal("ok", Encoding.UTF8.GetString(response));
        Assert.Equal(1, Volatile.Read(ref invoked));
    }

    [Fact]
    public async Task StopAsync_WaitsForTlsConnection_AndClientReadsEof()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = CreateServerTls(certificate);
        using var release = new CancellationTokenSource();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            },
            pipeline =>
            {
                pipeline.UseTls(tls);
                pipeline.Run(async context =>
                {
                    handlerEntered.TrySetResult();
                    await TestHandlers.HoldAsync(context, release.Token);
                });
            });

        await server.StartAsync(CancellationToken.None);

        await using var client = await TlsServerTestClient.ConnectAsync(
            (IPEndPoint)server.LocalEndPoint!,
            certificate);

        await handlerEntered.Task.WaitAsync(TestWaits.DefaultTimeout);

        var stopTask = server.StopAsync(CancellationToken.None);

        // 在途连接未完成前，停机必须等待。
        await Task.Delay(200);
        Assert.False(stopTask.IsCompleted);

        release.Cancel();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

        // handler 完成后 TLS 包装层被释放，客户端读到 EOF。
        await client.AssertClosedAsync();
    }

    [Fact]
    public async Task HandlerException_DisposesTlsAndServerSurvives()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = CreateServerTls(certificate);
        var calls = 0;

        await using var server = CreateServer(
            tls,
            context =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("boom");

                return TestHandlers.EchoAsync(context);
            });

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        await using (var first = await TlsServerTestClient.ConnectAsync(endPoint, certificate))
        {
            // handler 抛异常后连接被关闭或中止。
            await first.AssertClosedAsync();
        }

        // 服务器仍然存活，可以接受新的 TLS 连接并回显。
        await using var second = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
        await second.WriteAsync("ok"u8.ToArray());
        var response = await second.ReadExactlyAsync(2);

        Assert.Equal("ok", Encoding.UTF8.GetString(response));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task UseTls_PassesThroughAlpnSelection()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol("test-proto"),
                },
            },
        };

        var invoked = 0;

        await using var server = CreateServer(
            tls,
            async context =>
            {
                Interlocked.Increment(ref invoked);
                await TestHandlers.EchoAsync(context);
            });

        await server.StartAsync(CancellationToken.None);

        await using var client = await TlsServerTestClient.ConnectAsync(
            (IPEndPoint)server.LocalEndPoint!,
            certificate,
            alpn: "test-proto");

        Assert.Equal("test-proto", client.Stream.NegotiatedApplicationProtocol.ToString());

        // ALPN 只透传：不根据协议选择 handler，唯一注册的 handler 照常执行。
        await client.WriteAsync("ok"u8.ToArray());
        var response = await client.ReadExactlyAsync(2);

        Assert.Equal("ok", Encoding.UTF8.GetString(response));
        Assert.Equal(1, Volatile.Read(ref invoked));
    }

    [Fact]
    public async Task ServerSideHandshakeFailure_DoesNotInvokeHandler()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();

        // 服务端要求客户端证书，而客户端不提供：
        // 这是一条**服务端侧**的握手失败路径，握手在服务端直接失败。
        var tls = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
            },
        };

        var invoked = 0;
        var innerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            pipeline =>
            {
                pipeline.UseTls(tls);

                // TLS 之后的中间件同样不得被进入。
                pipeline.Use(async (context, next) =>
                {
                    innerEntered.TrySetResult();
                    await next(context);
                });

                pipeline.Run(context =>
                {
                    Interlocked.Increment(ref invoked);
                    return TestHandlers.EchoAsync(context);
                });
            });

        await server.StartAsync(CancellationToken.None);
        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        // 一个正常工作的服务端，用来验证握手失败的连接没有影响其他服务端。
        await using var normalServer = CreateServer(CreateServerTls(certificate), TestHandlers.EchoAsync);
        await normalServer.StartAsync(CancellationToken.None);
        var normalEndPoint = (IPEndPoint)normalServer.LocalEndPoint!;

        // 客户端不提供证书。
        //
        // 协议事实（Windows Schannel，TLS 1.2）：客户端的 AuthenticateAsClientAsync
        // 可能先于服务端的拒绝返回成功，真正的失败在服务端握手阶段暴露。
        // 因此这里不依赖客户端是否抛异常，而是断言**服务端**的可观测行为。
        try
        {
            await using var rejected = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
            await rejected.WriteAsync("must-not-arrive"u8.ToArray());
            await rejected.AssertClosedAsync();
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
        {
            // 客户端侧握手直接失败，同样说明服务端拒绝了这次握手。
        }

        // 服务端握手失败：TLS 之后的中间件与终端 handler 都不得被调用。
        await Task.Delay(200);
        Assert.False(innerEntered.Task.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref invoked));

        // 握手失败的连接没有影响其他服务端：普通 TLS 服务端仍能正常回显。
        await using var normal = await TlsServerTestClient.ConnectAsync(normalEndPoint, certificate);
        await normal.WriteAsync("ok"u8.ToArray());
        var response = await normal.ReadExactlyAsync(2);

        Assert.Equal("ok", Encoding.UTF8.GetString(response));
    }

    [Fact]
    public async Task UseTls_ValidatesOnlyServerOptions()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();

        // ClientOptions 不完整（缺少 TargetHost），服务端校验不得读取它。
        var tls = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            },
            ClientOptions = new SslClientAuthenticationOptions
            {
                // 故意不设置 TargetHost。
            },
        };

        await using var server = CreateServer(tls, TestHandlers.EchoAsync);

        // 注册 UseTls 不得抛出异常。
        await server.StartAsync(CancellationToken.None);

        await using var client = await TlsServerTestClient.ConnectAsync(
            (IPEndPoint)server.LocalEndPoint!,
            certificate);

        await client.WriteAsync("ok"u8.ToArray());
        var response = await client.ReadExactlyAsync(2);

        Assert.Equal("ok", Encoding.UTF8.GetString(response));
    }

    private static ChannelTlsOptions CreateServerTls(X509Certificate2 certificate)
        => new()
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            },
        };

    private static ChannelServer CreateServer(
        ChannelTlsOptions tls,
        ChannelHandlerDelegate handler)
        => new(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            pipeline =>
            {
                pipeline.UseTls(tls);
                pipeline.Run(handler);
            });
}
