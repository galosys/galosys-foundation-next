# channel-tls Specification

## Purpose
定义 Channel 的 TLS 传输底座：TLS 配置校验、`SslStream` 与 `IDuplexPipe` 之间的适配器、握手失败与超时，以及 TLS 关闭/半关闭语义。本阶段只提供可独立测试的适配器，不接入管线，也不提供客户端 TLS。

## Requirements

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

### Requirement: TLS 传输适配器

The implementation SHALL provide internal `TlsDuplexPipe` and `SslStreamDuplexPipe` types. `SslStreamDuplexPipe` MUST adapt an existing `IDuplexPipe` to a read/write `Stream` for `SslStream` without taking ownership of the inner transport. `TlsDuplexPipe` MUST authenticate an `SslStream` as server or client, expose decrypted `Input` and `Output` pipes, start read and write pumps only after successful authentication, preserve byte order, and propagate output completion to the inner transport after flushing. The adapter MUST support half-close: inner input EOF completes decrypted input while the write direction remains usable; output completion drains pending bytes, sends a TLS `close_notify`, and completes the inner output.

#### Scenario: 握手后双向传输

- **WHEN** 服务端与客户端分别用测试证书创建 `TlsDuplexPipe`
- **THEN** 双方握手成功
- **AND** 客户端写入的字节按顺序从服务端 `Input` 读出
- **AND** 服务端写入的字节按顺序从客户端 `Input` 读出

#### Scenario: 客户端半关闭后仍可收到响应

- **WHEN** 客户端完成自己的 `TlsDuplexPipe.Output`，服务端读到输入 EOF
- **THEN** 服务端仍可通过自己的 `Output` 写回响应
- **AND** 客户端在读到响应后才观察到输入结束

#### Scenario: 输出完成后发送 close_notify

- **WHEN** 调用方写入数据并完成 `TlsDuplexPipe.Output`
- **THEN** 写泵先刷新所有已提交字节，再调用 `SslStream.ShutdownAsync`
- **AND** 底层 `inner.Output` 随后完成，接收方读到干净 EOF

#### Scenario: 适配器不释放底层传输

- **WHEN** 释放 `TlsDuplexPipe` 或 `SslStreamDuplexPipe`
- **THEN** 适配器释放自己的 `SslStream` 与内部 `Pipe`
- **AND** 不调用底层 `IDuplexPipe.Input.Complete` 或 `Output.Complete` 之外的释放操作

### Requirement: TLS 握手失败与超时

The implementation SHALL fail `TlsDuplexPipe.CreateAsync` when authentication fails, the peer sends clear-text bytes, the certificate is rejected, the handshake exceeds `ChannelTlsOptions.HandshakeTimeout`, or the supplied `CancellationToken` is canceled. A failed handshake MUST NOT expose a usable transport, MUST dispose the `SslStream`, and MUST surface an `AuthenticationException`, `IOException`, or `OperationCanceledException`. Plaintext fallback is forbidden.

#### Scenario: 证书校验失败

- **WHEN** 客户端把 `RemoteCertificateValidationCallback` 配置为拒绝测试证书
- **THEN** 客户端 `CreateAsync` 抛出 `AuthenticationException`
- **AND** 不会返回可读写的 `TlsDuplexPipe`

#### Scenario: 明文连接被拒绝

- **WHEN** 客户端连接 TLS 端口后直接发送明文 HTTP 请求字节
- **THEN** 服务端握手失败并抛出 `AuthenticationException` 或 `IOException`
- **AND** 服务端不会把任何明文请求字节交给上层管线

#### Scenario: 握手超时

- **WHEN** 服务端设置 `HandshakeTimeout` 为 100 毫秒，客户端连接后不发送 TLS 握手字节
- **THEN** 服务端 `CreateAsync` 在超时后抛出 `OperationCanceledException`
- **AND** `SslStream` 被释放，底层连接可被框架中止

#### Scenario: 外部取消握手

- **WHEN** 调用方在握手完成前取消传入 `CreateAsync` 的 `CancellationToken`
- **THEN** `CreateAsync` 抛出 `OperationCanceledException`
- **AND** 不会启动读/写泵

### Requirement: TLS 关闭与释放

The implementation SHALL make `TlsDuplexPipe.DisposeAsync` idempotent. Disposal MUST complete the exposed output, wait for the write pump to drain within a bounded interval, complete the decrypted input, dispose the `SslStream` and internal pipes, and MUST NOT dispose the inner transport. After disposal, reads and writes MUST fail rather than hang. The abort path MAY discard unsent bytes; the graceful path MUST flush committed output before closing.

#### Scenario: 优雅释放排空输出

- **WHEN** 调用方写入大于输出 Pipe 阈值的数据后释放 `TlsDuplexPipe`
- **THEN** 对端在 EOF 前收到完整数据
- **AND** 数据顺序与写入顺序一致

#### Scenario: 重复释放

- **WHEN** 连续调用两次 `DisposeAsync`
- **THEN** 第二次调用正常完成，不抛异常
- **AND** 不会重复释放底层传输

#### Scenario: 中止丢弃未发送数据

- **WHEN** 底层连接被中止后释放 `TlsDuplexPipe`
- **THEN** 适配器不保证未发送数据被刷新
- **AND** 释放过程不阻塞超过有界等待时间

### Requirement: 服务端 UseTls 中间件

The implementation SHALL provide a public `UseTls(this IChannelPipeline pipeline, ChannelTlsOptions options)` extension method in `Galosys.Foundation.Channel`. The middleware MUST create and authenticate a server-side `TlsDuplexPipe` from `context.Transport` before invoking `next`, MUST replace the transport with `context.UseTransport(tlsPipe)` after a successful handshake, and MUST invoke `next` exactly once. The middleware MUST dispose the TLS wrapper in a `finally` block after the pipeline completes. It MUST NOT read business bytes, MUST NOT create a session, and MUST NOT fall back to plaintext.

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
