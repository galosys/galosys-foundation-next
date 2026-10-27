## MODIFIED Requirements

### Requirement: TLS 配置

The implementation SHALL provide a public sealed `ChannelTlsOptions` in `Microsoft.Extensions.Options` with `SslServerAuthenticationOptions? ServerOptions`, `SslClientAuthenticationOptions? ClientOptions`, and `TimeSpan HandshakeTimeout` defaulting to 10 seconds. The options MUST validate that at least one side is configured, that `HandshakeTimeout` is greater than zero and at most one day, that server options provide a certificate, certificate context, or certificate selection callback, and that client options provide a non-empty `TargetHost`. Server middleware MUST validate the server side before the pipeline starts; client options MUST remain unused by server middleware and are reserved for the client change. Validation MUST fail before any handshake starts.

#### Scenario: 默认握手超时

- **WHEN** 创建 `ChannelTlsOptions` 并只设置 `ServerOptions`
- **THEN** `HandshakeTimeout` 为 10 秒
- **AND** 服务端校验通过

#### Scenario: 缺少服务端证书

- **WHEN** `ServerOptions` 非空，但 `ServerCertificate`、`ServerCertificateContext`、`ServerCertificateSelectionCallback` 都未设置
- **THEN** 服务端校验抛出 `ArgumentException`
- **AND** 不会创建 `SslStream` 或进入握手

#### Scenario: 非法握手超时

- **WHEN** `HandshakeTimeout` 为 0、负值或超过 1 天
- **THEN** `Validate` 抛出 `ArgumentException`

#### Scenario: 客户端目标主机缺失

- **WHEN** `ClientOptions` 非空但 `TargetHost` 为 null、空字符串或空白
- **THEN** `ValidateClient` 抛出 `ArgumentException`
- **AND** 不会发起客户端握手

#### Scenario: 服务端中间件只校验服务端配置

- **WHEN** `ChannelTlsOptions` 同时包含 `ServerOptions` 与不完整的 `ClientOptions`，并调用 `pipeline.UseTls(options)`
- **THEN** 服务端校验通过，管线可以启动
- **AND** `UseTls` 不会读取或校验 `ClientOptions`

## ADDED Requirements

### Requirement: 服务端 UseTls 中间件

The implementation SHALL provide a public `UseTls(this IChannelPipeline pipeline, ChannelTlsOptions options)` extension method in `Microsoft.Extensions.Hosting.Net`. The middleware MUST create and authenticate a server-side `TlsDuplexPipe` from `context.Transport` before invoking `next`, MUST replace the transport with `context.UseTransport(tlsPipe)` after a successful handshake, and MUST invoke `next` exactly once. The middleware MUST dispose the TLS wrapper in a `finally` block after the pipeline completes. It MUST NOT read business bytes, MUST NOT create a session, and MUST NOT fall back to plaintext.

#### Scenario: TLS 回显

- **WHEN** `ChannelServer` 使用 `pipeline.UseTls(options); pipeline.Run(echoHandler)` 启动，客户端用受信任的测试证书完成握手
- **THEN** 客户端写入的字节被完整回显
- **AND** `context.Reader` / `context.Writer` 来自 `TlsDuplexPipe`，不是底层明文管道

#### Scenario: 证书校验失败中止连接

- **WHEN** 客户端拒绝服务端测试证书，握手失败
- **THEN** 终端 handler 不被调用
- **AND** 该连接被中止，服务器继续接受后续连接
- **AND** 后续使用正确证书的客户端仍能完成 TLS 回显

#### Scenario: 握手先于后续中间件

- **WHEN** 管线依次注册“TLS 前观察中间件”、`UseTls`、“TLS 后观察中间件”和 handler，客户端先建立 TCP 连接但延迟 TLS 握手
- **THEN** 只有 TLS 前观察中间件已经进入
- **AND** 客户端完成握手后才执行 TLS 后观察中间件和 handler
- **AND** 退出顺序为 handler → TLS 后中间件 → TLS 前中间件

#### Scenario: 明文请求不进入 handler

- **WHEN** 客户端连接 TLS 端口后直接发送明文 HTTP 请求字节
- **THEN** 服务端握手失败，终端 handler 不被调用
- **AND** 服务端不回显任何明文请求内容
- **AND** 连接被关闭或中止

#### Scenario: TLS 活跃时优雅停机

- **WHEN** 一个 TLS 连接的处理尚未完成，调用方调用 `ChannelServer.StopAsync`
- **THEN** 服务器停止接受新连接并等待在途连接
- **AND** handler 完成后 `TlsDuplexPipe` 被释放，客户端读到完整响应与 EOF
- **AND** `StopAsync` 在 `ShutdownTimeout` 内返回

#### Scenario: ALPN 只透传

- **WHEN** 服务端与客户端都在 `Ssl*AuthenticationOptions.ApplicationProtocols` 中配置同一个协议
- **THEN** 握手成功且 `SslStream.NegotiatedApplicationProtocol` 为该协议
- **AND** `UseTls` 不根据 ALPN 选择 handler，也不改变管线顺序
