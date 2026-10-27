## Why

`Galosys.Foundation.Primitives` 的 P1 传输只有明文 socket；P2 的 framer/codec/session 直接消费 `ChannelContext.Transport`。如果 TLS 不先收敛成一个“把现有 `IDuplexPipe` 包装成已认证管道”的适配器，后续 `UseTls` 中间件和客户端 TLS 会各自复制 `SslStream` 的握手、读写泵、关闭与超时逻辑，P2 的消息层也无法在 TLS 之上保持零改动。

本 change 只做 TLS 传输底座：配置类型、`SslStream` 与 `IDuplexPipe` 之间的适配器、自签名证书测试。管线接入留给 `primitives-tls-transport-2`，客户端接线留给 P4。

## What Changes

- 新增 public sealed `ChannelTlsOptions`（`Microsoft.Extensions.Options`），包含 `SslServerAuthenticationOptions? ServerOptions`、`SslClientAuthenticationOptions? ClientOptions`、`TimeSpan HandshakeTimeout`（默认 10 秒），并提供服务端/客户端校验。
- 新增 internal `SslStreamDuplexPipe`：把现有 `IDuplexPipe` 适配成 `SslStream` 可读写的 `Stream`，不拥有底层管道。
- 新增 internal `TlsDuplexPipe`：在现有 `IDuplexPipe` 上完成服务端或客户端 `SslStream` 握手，随后用读/写泵在加密流与解密后的 `Pipe` 之间搬运字节。
- 定义半关闭与刷新语义：输入 EOF 完成解密输入；输出完成先刷新并发送 TLS `close_notify`，再把完成信号传给底层输出；`DisposeAsync` 幂等且不释放底层传输。
- 新增自签名证书测试：服务端/客户端握手、证书拒绝、握手超时、外部取消、明文连接拒绝、优雅关闭/刷新与大消息顺序。
- 更新 Primitives README，记录 `ChannelTlsOptions` 与适配器的使用边界；明确本阶段不提供 `UseTls`。
- 本 change 不实现 `UseTls`、不做客户端连接、不做同端口 ALPN、不改 P2 的 framer/codec/session 契约。

## Capabilities

### New Capabilities

- `channel-tls`: TLS 配置校验、`SslStream`/`IDuplexPipe` 适配器、握手失败与超时、TLS 关闭/半关闭语义。

### Modified Capabilities

无。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelTlsOptions.cs`、`Microsoft/Extensions/Hosting/Net/Transports/SslStreamDuplexPipe.cs`、`Microsoft/Extensions/Hosting/Net/Transports/TlsDuplexPipe.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `ChannelTlsOptions`；`TlsDuplexPipe` / `SslStreamDuplexPipe` 保持 internal，不出现在公共签名中。
- 依赖：仅使用 .NET 基础库 `System.Net.Security`、`System.Security.Cryptography.X509Certificates`、`System.IO.Pipelines`，不新增 NuGet 包，不引入 `Microsoft.AspNetCore.*`。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/` 新增 `ChannelTlsOptionsTests.cs`、`TlsDuplexPipeTests.cs`、`TlsTestCertificates.cs`；现有测试不改。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`、`openspec validate "primitives-tls-transport-1" --strict --no-interactive`。
