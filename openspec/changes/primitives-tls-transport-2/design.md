## Context

`primitives-tls-transport-1` 交付了 `ChannelTlsOptions` 与 internal `TlsDuplexPipe`，但适配器还没有调用方。`ChannelServer` 的管线是 `IChannelPipeline.Use(...).Run(...)`；中间件契约要求只包装 `Transport`，不得读取业务数据。TLS 必须在这个缝里完成，才能让 P2 的 session 在 `context.AsSession(...)` 时看到解密后的管道。

本 change 增加服务端 `UseTls`，并锁定“握手先于 next、失败不进入业务、结束后释放包装层、明文不落到 handler”的行为。客户端 TLS 与同端口 ALPN 不在本 change。

## Goals / Non-Goals

**Goals:**

- 提供 public `UseTls(this IChannelPipeline, ChannelTlsOptions)`，调用方在 `ChannelServer` 的配置回调中像普通中间件一样使用。
- 在 `next` 之前完成服务端握手；握手失败时不调用 `next`，连接由 `ChannelServer` 中止。
- 成功后用 `context.UseTransport(tlsPipe)` 替换传输；`next` 只看到解密后的 `Reader`/`Writer`。
- 在 `finally` 中释放 `TlsDuplexPipe`，保证正常返回、handler 异常、短路和停机都走同一释放路径。
- 用真实 loopback + 自签名证书覆盖回显、证书拒绝、顺序、明文拒绝、优雅停机和 ALPN 透传。

**Non-Goals:**

- 不实现客户端 TLS；`ChannelClientOptions.Tls` 在 `primitives-client-1` 接线。
- 不做同端口 ALPN 协议选择，不改 P2.5 的 dispatcher。
- 不做 SNI 多证书选择、证书热更新、双向客户端证书的业务策略。
- 不读取或缓存业务字节；不在中间件里创建 session。
- 不改 `ChannelContext`、`IChannelPipeline`、`IChannelServer` 的公共契约。

## Decisions

### 1. 用扩展方法添加中间件，不新增中间件类型

```csharp
namespace Microsoft.Extensions.Hosting.Net;

public static class TlsPipelineExtensions
{
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
```

- 不引入 `TlsMiddleware` 公共类型：中间件没有独立生命周期，扩展方法已经能表达“加一层包装”。
- 在注册时调用 `ValidateServer()`，让缺少证书的配置在 `StartAsync` 构建管线时失败，而不是等到第一个连接。
- 闭包捕获 `options`，与 `ChannelServer` 的 pipeline 配置只执行一次相吻合。

### 2. 握手必须在 `next` 之前，且只做传输包装

- `TlsDuplexPipe.CreateAsync` 完成 `SslStream.AuthenticateAsServerAsync` 后才返回；返回前不会调用 `next`。
- 握手失败时异常直接冒泡到 `ChannelPipeline` 外层，`ChannelServer.ProcessConnectionAsync` 按现有逻辑中止连接；`next` 和终端 handler 都不会执行。
- 握手成功后 `context.UseTransport(tls)`；`context.Reader` / `context.Writer` 自动指向 `TlsDuplexPipe.Input` / `Output`。
- 中间件不调用 `context.Reader.ReadAsync`，不偷看首字节，不创建 `IChannelSession<T>`；业务数据只由终端 handler 消费。
- `context.ConnectionClosed` 作为外部取消令牌传入握手；服务器停机或连接被中止时，握手会被取消，不会留下悬挂的 `SslStream`。

### 3. `finally` 释放包装层，底层传输仍由服务器释放

- `TlsDuplexPipe.DisposeAsync` 先排空输出、发送 `close_notify`，再释放 `SslStream` 和内部 `Pipe`；不释放 `context` 的底层 `IChannelDuplexPipe`。
- `ChannelServer` 在 handler 返回后调用 `ChannelContext.BeginCloseAsync` → 完成当前 `Transport.Output`（即 TLS 包装层的 `Output`）→ 等待发送排空 → 释放底层连接。顺序必须是“TLS 包装层先完成自己的输出，底层连接后收尾”。
- 如果 `next` 抛异常，`finally` 仍释放 TLS 包装层；`ChannelServer` 随后按异常路径中止底层连接。
- 如果中间件短路（本 change 不提供短路选项，但 `UseTls` 后面的中间件可以），`finally` 仍执行。

### 4. 顺序约束与测试

推荐顺序：

```csharp
pipeline.UseTls(tlsOptions);
pipeline.Use(telemetry);
pipeline.Use(timeout);
pipeline.Run(handler);
```

- TLS 应尽量靠前；在 TLS 之前只放不读业务字节的观察型中间件（日志、连接计数）。如果在 TLS 之前放认证/限流中间件，它看到的是加密字节，不能按明文语义工作。
- 测试同时覆盖“TLS 前中间件进入 → 握手完成 → TLS 后中间件进入 → handler”的顺序；用一个延迟握手的客户端证明 `UseTls` 后面的中间件不会提前执行。
- 不强制在 `UseTls` 内部检查“是否已有 TLS”；同一管线重复 `UseTls` 会导致二次握手失败，文档明确只调用一次。

### 5. ALPN 只透传，不做同端口协议选择

- `SslServerAuthenticationOptions.ApplicationProtocols` 原样交给 `SslStream`；客户端可以在 `SslClientAuthenticationOptions.ApplicationProtocols` 中声明协议，握手后通过 `SslStream.NegotiatedApplicationProtocol` 观察结果。
- 本 change 不根据 ALPN 选择 handler，也不把 ALPN 结果写入 `ChannelContext.Items` 作为协议分发依据；同端口多协议留给 P2.5/P3 的后续 change。
- 测试只验证 ALPN 选项能协商出预期协议，不验证协议分发。

### 6. 客户端选项共享，但不在本 change 接线

`ChannelTlsOptions.ClientOptions` 与 `primitives-client-1` 共用同一个配置类型；`UseTls` 只调用 `ValidateServer()`，不会读取 `ClientOptions`。这样服务端和客户端可以各自创建 `ChannelTlsOptions`，也可以在同一配置对象上同时配置两侧，但 `UseTls` 不会误用客户端证书。

## Risks / Trade-offs

- **握手阻塞连接处理** → 每个连接在管线开始时同步等待握手；这是 TLS 的必然成本。`HandshakeTimeout` 防止慢速/恶意客户端长期占用连接。
- **停机时握手悬挂** → `context.ConnectionClosed` 参与握手取消；服务器 `StopAsync` 中止连接后握手会退出。
- **包装层与底层重复释放** → `TlsDuplexPipe` 只释放自己的 `SslStream`/`Pipe`；底层由 `ChannelServer` 释放；测试覆盖正常关闭和异常路径。
- **TLS 1.3 的 `close_notify` 时序** → 以 `SslStream.ShutdownAsync` + 底层 FIN 为准；测试断言对端读到完整数据和 EOF，不依赖具体 TLS 版本。
- **明文客户端收到的是连接关闭而不是 HTTP 错误** → 这是预期行为；TLS 端口不提供明文回退，测试锁定“handler 不被调用、无回显”。
- **ALPN 被误解为已支持协议分发** → README 与 spec 明确“只透传，不做选择”；测试只断言协商结果。
- **证书测试依赖本机 TLS 栈** → 使用自签名 RSA 2048/SHA-256 与默认协议协商，不强制套件；测试不访问外部网络。

## Migration Plan

1. 新增 `TlsPipelineExtensions`，先在测试中确认 `UseTls` 不存在时编译失败。
2. 用 `ChannelServer` + `UseTls` + echo handler 写首个 TLS 回显测试，确认握手、替换传输和释放顺序。
3. 补证书拒绝、明文拒绝、顺序、停机和 ALPN 测试。
4. 更新 Primitives README 的管线章节，加入 `UseTls` 示例和顺序约束；sample 保持明文。
5. 跑 Primitives 构建与全量测试；若失败，回滚新增扩展方法和测试/README，TLS 适配器保持不变。

## Open Questions

无。客户端 TLS、同端口 ALPN、SNI 多证书和协议分发明确留给后续 change。
