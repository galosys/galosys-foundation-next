# P3-2 服务端 UseTls 中间件 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 `UseTls(this IChannelPipeline, ChannelTlsOptions)`，在 `next` 前完成服务端握手、替换 `ChannelContext.Transport`，并在管线结束后释放 TLS 包装层；用真实 loopback TLS 客户端锁定回显、失败、顺序、明文拒绝、停机与 ALPN 透传。

**Architecture:** `UseTls` 是 `IChannelPipeline` 扩展方法，内部注册一个只做传输包装的 `ChannelMiddleware`。中间件调用 `TlsDuplexPipe.CreateAsync(..., isServer: true, context.ConnectionClosed)`，成功后 `context.UseTransport(tls)`，`finally` 中 `await tls.DisposeAsync()`。握手失败不调用 `next`，由 `ChannelServer` 中止连接。

**Tech Stack:** .NET 10、`System.Net.Security`、`System.Net.Sockets`、`System.IO.Pipelines`、xunit 2.9.3。

---

## 1. 扩展方法与首个 TLS 回显

- [ ] 1.1 写 `UseTls` 失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs`

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class TlsServerIntegrationTests
{
    [Fact]
    public async Task UseTls_EchoesBytes()
    {
        using var certificate = TlsTestCertificates.CreateSelfSigned();
        var tls = new ChannelTlsOptions
        {
            ServerOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
            },
        };

        await using var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline =>
            {
                pipeline.UseTls(tls);
                pipeline.Run(TestHandlers.EchoAsync);
            });

        await server.StartAsync(CancellationToken.None);

        await using var client = await TlsServerTestClient.ConnectAsync(
            (IPEndPoint)server.LocalEndPoint!,
            certificate);

        await client.Stream.WriteAsync("ping"u8.ToArray());
        var buffer = new byte[4];
        await client.Stream.ReadExactlyAsync(buffer);

        Assert.Equal("ping", Encoding.UTF8.GetString(buffer));
    }
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~TlsServerIntegrationTests"`
Expected: 编译失败，提示 `UseTls` 不存在（这是本任务的“红”）。

- [ ] 1.2 新增 `TlsPipelineExtensions`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/TlsPipelineExtensions.cs`

```csharp
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>TLS 服务端管线扩展。</summary>
    public static class TlsPipelineExtensions
    {
        /// <summary>在管线开头完成服务端 TLS 握手，并用解密后的传输替换上下文传输。</summary>
        public static IChannelPipeline UseTls(
            this IChannelPipeline pipeline,
            ChannelTlsOptions options)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(options);
            options.ValidateServer();

            return pipeline.Use((context, next) => InvokeAsync(context, next, options));
        }

        private static async Task InvokeAsync(
            ChannelContext context,
            ChannelHandlerDelegate next,
            ChannelTlsOptions options)
        {
            var tls = await TlsDuplexPipe.CreateAsync(
                context.Transport,
                options,
                isServer: true,
                context.ConnectionClosed).ConfigureAwait(false);

            context.UseTransport(tls);

            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
```

- [ ] 1.3 构建并跑首个 TLS 回显测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~UseTls_EchoesBytes"`
Expected: 测试通过。

## 2. 集成行为测试

- [ ] 2.1 增加 TLS 测试客户端辅助类

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsTestCertificates.cs`

在文件末尾追加：

```csharp
internal sealed class TlsServerTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;

    private TlsServerTestClient(TcpClient tcp, SslStream stream)
    {
        _tcp = tcp;
        Stream = stream;
    }

    public SslStream Stream { get; }

    public static async Task<TlsServerTestClient> ConnectAsync(
        IPEndPoint endPoint,
        X509Certificate2 certificate,
        string? alpn = null)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(endPoint.Address, endPoint.Port);
        var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            (_, remote, _, _) =>
                remote is not null
                && remote.GetCertHashString(HashAlgorithmName.SHA256)
                    == certificate.GetCertHashString(HashAlgorithmName.SHA256));

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
        };

        if (alpn is not null)
        {
            options.ApplicationProtocols = new List<SslApplicationProtocol>
            {
                new SslApplicationProtocol(alpn),
            };
        }

        await ssl.AuthenticateAsClientAsync(options);
        return new TlsServerTestClient(tcp, ssl);
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _tcp.Dispose();
    }
}
```

补 using：`System.Net.Security`、`System.Net.Sockets`、`System.Text` 中实际使用的项。

- [ ] 2.2 增加证书拒绝与服务器恢复测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs`

```csharp
[Fact]
public async Task CertificateRejected_DoesNotInvokeHandler_AndServerAcceptsNextClient()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var tls = CreateServerTls(certificate);
    var invoked = 0;

    await using var server = CreateServer(tls, async context =>
    {
        Interlocked.Increment(ref invoked);
        await TestHandlers.EchoAsync(context);
    });
    await server.StartAsync(CancellationToken.None);
    var endPoint = (IPEndPoint)server.LocalEndPoint!;

    using (var tcp = new TcpClient())
    {
        await tcp.ConnectAsync(endPoint.Address, endPoint.Port);
        await using var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            (_, _, _, _) => false);

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "localhost" }));
    }

    await Task.Delay(100);
    Assert.Equal(0, Volatile.Read(ref invoked));

    await using var good = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
    await good.Stream.WriteAsync("ok"u8.ToArray());
    var buffer = new byte[2];
    await good.Stream.ReadExactlyAsync(buffer);

    Assert.Equal("ok", Encoding.UTF8.GetString(buffer));
}
```

同时加入本地辅助方法：

```csharp
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
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        },
        pipeline =>
        {
            pipeline.UseTls(tls);
            pipeline.Run(handler);
        });
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~CertificateRejected"`
Expected: 测试通过；handler 未执行，后续合法客户端仍可回显。

- [ ] 2.3 增加管线顺序测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs`

```csharp
[Fact]
public async Task TlsHandshake_RunsBeforeInnerMiddlewareAndHandler()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var tls = CreateServerTls(certificate);
    var outerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var innerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var events = new List<string>();

    await using var server = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        },
        pipeline =>
        {
            pipeline.Use(async (context, next) =>
            {
                lock (events) events.Add("outer-enter");
                outerEntered.TrySetResult();
                await next(context);
                lock (events) events.Add("outer-exit");
            });
            pipeline.UseTls(tls);
            pipeline.Use(async (context, next) =>
            {
                lock (events) events.Add("inner-enter");
                innerEntered.TrySetResult();
                await next(context);
                lock (events) events.Add("inner-exit");
            });
            pipeline.Run(context =>
            {
                lock (events) events.Add("handler");
                return Task.CompletedTask;
            });
        });

    await server.StartAsync(CancellationToken.None);
    var endPoint = (IPEndPoint)server.LocalEndPoint!;

    using var tcp = new TcpClient();
    await tcp.ConnectAsync(endPoint.Address, endPoint.Port);
    await outerEntered.Task.WaitAsync(TestWaits.DefaultTimeout);

    Assert.False(innerEntered.Task.IsCompleted);

    await using var ssl = new SslStream(
        tcp.GetStream(),
        leaveInnerStreamOpen: false,
        (_, remote, _, _) =>
            remote is not null
            && remote.GetCertHashString(HashAlgorithmName.SHA256)
                == certificate.GetCertHashString(HashAlgorithmName.SHA256));

    await ssl.AuthenticateAsClientAsync(
        new SslClientAuthenticationOptions { TargetHost = "localhost" });

    await innerEntered.Task.WaitAsync(TestWaits.DefaultTimeout);
    await TestClient.WaitUntilAsync(
        () => Task.FromResult(events.Contains("handler")),
        "handler 未执行。");

    lock (events)
    {
        Assert.Equal(
            new[] { "outer-enter", "inner-enter", "handler", "inner-exit", "outer-exit" },
            events);
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~TlsHandshake_RunsBeforeInnerMiddlewareAndHandler"`
Expected: 测试通过；握手完成前 inner 中间件和 handler 都不执行。

- [ ] 2.4 增加明文请求不泄漏测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs`

```csharp
[Fact]
public async Task ClearTextRequest_DoesNotReachHandler()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var tls = CreateServerTls(certificate);
    var invoked = 0;

    await using var server = CreateServer(tls, context =>
    {
        Interlocked.Increment(ref invoked);
        return TestHandlers.EchoAsync(context);
    });
    await server.StartAsync(CancellationToken.None);
    var endPoint = (IPEndPoint)server.LocalEndPoint!;

    using (var tcp = new TcpClient())
    {
        await tcp.ConnectAsync(endPoint.Address, endPoint.Port);
        await tcp.GetStream().WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        await Task.Delay(100);

        var buffer = new byte[1];
        var read = await tcp.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, read);
    }

    Assert.Equal(0, Volatile.Read(ref invoked));

    await using var good = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
    await good.Stream.WriteAsync("ok"u8.ToArray());
    var response = new byte[2];
    await good.Stream.ReadExactlyAsync(response);
    Assert.Equal("ok", Encoding.UTF8.GetString(response));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ClearTextRequest_DoesNotReachHandler"`
Expected: 测试通过；明文请求没有回显，handler 未执行。

- [ ] 2.5 增加 TLS 活跃时优雅停机测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs`

```csharp
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
            ShutdownTimeout = TimeSpan.FromSeconds(5),
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
    await Task.Delay(100);
    Assert.False(stopTask.IsCompleted);

    release.Cancel();
    await stopTask.WaitAsync(TestWaits.DefaultTimeout);

    var buffer = new byte[1];
    var read = await client.Stream.ReadAsync(buffer).AsTask().WaitAsync(TestWaits.DefaultTimeout);
    Assert.Equal(0, read);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~StopAsync_WaitsForTlsConnection"`
Expected: 测试通过；`StopAsync` 等待 handler 完成，客户端读到 EOF。

- [ ] 2.6 增加 handler 异常释放与 ALPN 透传测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs`

```csharp
[Fact]
public async Task HandlerException_DisposesTlsAndServerSurvives()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();
    var tls = CreateServerTls(certificate);
    var calls = 0;

    await using var server = CreateServer(tls, context =>
    {
        if (Interlocked.Increment(ref calls) == 1)
            throw new InvalidOperationException("boom");

        return TestHandlers.EchoAsync(context);
    });
    await server.StartAsync(CancellationToken.None);
    var endPoint = (IPEndPoint)server.LocalEndPoint!;

    await using (var first = await TlsServerTestClient.ConnectAsync(endPoint, certificate))
    {
        var buffer = new byte[1];
        var read = await first.Stream.ReadAsync(buffer).AsTask().WaitAsync(TestWaits.DefaultTimeout);
        Assert.Equal(0, read);
    }

    await using var second = await TlsServerTestClient.ConnectAsync(endPoint, certificate);
    await second.Stream.WriteAsync("ok"u8.ToArray());
    var response = new byte[2];
    await second.Stream.ReadExactlyAsync(response);
    Assert.Equal("ok", Encoding.UTF8.GetString(response));
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

    await using var server = CreateServer(tls, TestHandlers.EchoAsync);
    await server.StartAsync(CancellationToken.None);

    await using var client = await TlsServerTestClient.ConnectAsync(
        (IPEndPoint)server.LocalEndPoint!,
        certificate,
        alpn: "test-proto");

    Assert.Equal("test-proto", client.Stream.NegotiatedApplicationProtocol.ToString());
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~HandlerException_DisposesTls|FullyQualifiedName~UseTls_PassesThroughAlpnSelection"`
Expected: 两个测试通过；handler 异常后服务器仍可接受新 TLS 连接，ALPN 协商为 `test-proto`。

## 3. 文档与验证

- [ ] 3.1 更新 Primitives README 的 `UseTls` 章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

增加：

```markdown
## 服务端 TLS（P3-2）

```csharp
var tls = new ChannelTlsOptions
{
    ServerOptions = new SslServerAuthenticationOptions
    {
        ServerCertificate = certificate,
    },
};

var server = new ChannelServer(options, pipeline =>
{
    pipeline.UseTls(tls);
    pipeline.Use(telemetry);
    pipeline.Run(handler);
});
```

`UseTls` 在 `next` 之前完成服务端握手，并用 `context.UseTransport` 替换为解密后的管道；
握手失败不会调用后续中间件或 handler。中间件在管线结束后释放 TLS 包装层，底层连接仍由
`ChannelServer` 释放。

- TLS 中间件应尽量靠前；在它之前只放不读业务字节的观察型中间件。
- 同一管线只调用一次 `UseTls`；重复调用会导致二次握手失败。
- `ApplicationProtocols` 只透传给 `SslStream`，本阶段不做同端口 ALPN 协议分发。
- `NetServer.ConsoleApp` 保持明文示例，不在此 change 启用 TLS。
```

- [ ] 3.2 跑 Primitives 构建与全量测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `TlsServerIntegrationTests` 全部通过。

- [ ] 3.3 校验 OpenSpec artifact

Run: `openspec validate "primitives-tls-transport-2" --strict --no-interactive`
Expected: `Change 'primitives-tls-transport-2' is valid`。
