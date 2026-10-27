## Why

`primitives-tls-transport-1` 已提供 `ChannelTlsOptions` 与 `TlsDuplexPipe`，但还没有把适配器接到 `IChannelPipeline`。调用方如果自己在 handler 里包 `SslStream`，会绕过 `ChannelContext.UseTransport`、管线顺序和框架的连接收尾，P2 的 session 也会在明文管道上创建。需要在 `next` 之前完成握手，把 TLS 管道替换进上下文，并在管线结束后释放包装层。

本 change 只做服务端 `UseTls` 中间件；客户端 TLS 接线在 P4，同端口 ALPN 选择留给后续 change。

## What Changes

- 新增 public `UseTls(this IChannelPipeline pipeline, ChannelTlsOptions options)` 扩展方法（`Microsoft.Extensions.Hosting.Net`）。
- 中间件在调用 `next` 前用 `TlsDuplexPipe.CreateAsync(..., isServer: true, ...)` 完成握手，成功后调用 `context.UseTransport(tlsPipe)`。
- 中间件在 `finally` 中释放 `TlsDuplexPipe`；握手失败时不调用 `next`，异常交给 `ChannelServer` 中止连接。
- 中间件只包装传输，不读取业务字节；握手超时复用 `ChannelTlsOptions.HandshakeTimeout`，连接关闭令牌参与取消。
- 新增服务端集成测试：TLS 回显、证书拒绝、管线顺序、明文不泄漏、TLS 活跃时优雅停机、ALPN 选项透传。
- 更新 Primitives README，增加 `UseTls` 用法与顺序约束；`NetServer.ConsoleApp` 保持明文示例，不在此 change 启用 TLS。
- 明确 ALPN 仅为透传：`SslServerAuthenticationOptions.ApplicationProtocols` 会交给 `SslStream`，但本 change 不做同端口按 ALPN 选协议。
- 本 change 不实现客户端 TLS；`ChannelTlsOptions.ClientOptions` 由 `primitives-client-1` 使用。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `channel-tls`: 增加服务端 `UseTls` 中间件要求，明确握手先于 `next`、传输替换、`finally` 释放、失败中止、明文拒绝与 ALPN 预留。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Pipeline/TlsPipelineExtensions.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `UseTls` 扩展方法；`TlsDuplexPipe` 仍为 internal。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/TlsServerIntegrationTests.cs` 新增 TLS 服务端集成测试；复用 `TlsTestCertificates`。
- 依赖：依赖 `primitives-tls-transport-1` 与 `primitives-server-composition-2`（测试使用 sealed `ChannelServer`）；不新增 NuGet 包。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-tls-transport-2" --strict --no-interactive`。
