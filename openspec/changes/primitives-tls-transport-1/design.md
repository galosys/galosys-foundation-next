## Context

P1 的 `SocketChannelConnection` 把 socket 字节写入 `Pipe`，`ChannelContext.Transport` 暴露的就是这对明文管道；P2 的 `IChannelSession<T>` 只在终端处理器里消费这对管道，不知道底层是 TCP 还是 TLS。设计文档 `2026-10-20-primitives-socket-transport-design.md` 把 TLS 定为 L4 中间件，顺序是 `scope → TLS → telemetry → timeout → handler`，因此 TLS 必须是一个“包装 `IDuplexPipe`、不读业务数据”的传输层组件。

当前仓库没有任何 TLS 代码，也没有 `UseTls`。本 change 先交付可独立测试的 TLS 配置与适配器；管线中间件在 `primitives-tls-transport-2` 接入，客户端在 P4 使用同一个 `ChannelTlsOptions.ClientOptions`。

## Goals / Non-Goals

**Goals:**

- 提供 public `ChannelTlsOptions`，复用 BCL 的 `SslServerAuthenticationOptions` / `SslClientAuthenticationOptions`，不自定义证书模型。
- 提供 internal `SslStreamDuplexPipe`（`IDuplexPipe` → `Stream`）与 `TlsDuplexPipe`（认证后的 `IDuplexPipe`），把 `SslStream` 的握手、读写泵、刷新、半关闭和释放收敛到一处。
- 握手超时可配置且有默认值；外部取消、证书拒绝、明文连接都必须失败关闭，不得回退明文。
- 用自签名证书和真实 loopback socket 覆盖成功与失败路径，测试不依赖本机证书库或外部网络。
- 不改变现有 `ChannelContext`、`IChannelServer`、`IChannelPipeline` 和 P2 session 的契约。

**Non-Goals:**

- 不提供 `UseTls` 扩展方法；本 change 不把适配器挂到管线。
- 不实现客户端 `ChannelClient` 的 TLS 接线；`ClientOptions` 只做校验和预留。
- 不做同端口 ALPN、协议探测或多证书 SNI 选择。
- 不做证书热更新、OCSP、双向客户端证书的业务策略；这些由调用方通过 `Ssl*AuthenticationOptions` 自行配置。
- 不改 P2 的 framer/codec/session。

## Decisions

### 1. `ChannelTlsOptions` 复用 BCL 的 `Ssl*AuthenticationOptions`

```csharp
namespace Microsoft.Extensions.Options;

public sealed class ChannelTlsOptions
{
    public SslServerAuthenticationOptions? ServerOptions { get; set; }

    public SslClientAuthenticationOptions? ClientOptions { get; set; }

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate();
    internal void ValidateServer();
    internal void ValidateClient();
}
```

- 不把证书、协议版本、校验回调拆成自定义属性。`SslServerAuthenticationOptions` 已经覆盖证书、`ClientCertificateRequired`、`EnabledSslProtocols`、`ApplicationProtocols`、`CertificateRevocationCheckMode` 等；再包一层只会让调用方学两套 API。
- `Validate()` 做与角色无关的检查：至少配置一侧；`HandshakeTimeout` 大于 0 且不超过 1 天。
- `ValidateServer()` 要求 `ServerOptions` 非空，且 `ServerCertificate`、`ServerCertificateSelectionCallback`、`ServerCertificateContext` 至少有一个；`ValidateClient()` 要求 `ClientOptions` 非空且 `TargetHost` 非空白。
- 校验在创建适配器/中间件时执行，失败抛 `ArgumentException`，不进入握手。
- 不克隆 `Ssl*AuthenticationOptions`：它们包含回调与证书引用，深拷贝语义不清晰。文档要求调用方在服务器/客户端启动后不要修改同一实例；需要多个配置时分别创建。

### 2. 拆成 `SslStreamDuplexPipe` 与 `TlsDuplexPipe` 两层

`SslStreamDuplexPipe` 是 `Stream` 适配器，只负责“加密字节的读写”：

```csharp
internal sealed class SslStreamDuplexPipe : Stream
{
    private readonly IDuplexPipe _pipe;

    public SslStreamDuplexPipe(IDuplexPipe pipe) => _pipe = pipe;

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => _pipe.Input.ReadAsync(buffer, cancellationToken);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
        => WriteAndFlushAsync(buffer, cancellationToken);

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
}
```

`TlsDuplexPipe` 是 `IDuplexPipe`，持有 `SslStream`、解密后的输入/输出 `Pipe` 和两个泵：

```csharp
internal sealed class TlsDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    public static ValueTask<TlsDuplexPipe> CreateAsync(
        IDuplexPipe inner,
        ChannelTlsOptions options,
        bool isServer,
        CancellationToken cancellationToken);

    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public ValueTask DisposeAsync();
}
```

- 拆开的原因：`SslStream` 只认 `Stream`，而管线的契约是 `IDuplexPipe`。把两者压成一个类会让握手、泵和测试都难以单独定位。
- `SslStreamDuplexPipe` 的 `WriteAsync` 必须立即 `FlushAsync` 底层输出；否则握手字节会停在 `Pipe` 里，双方互等。
- `SslStreamDuplexPipe.Dispose` 不释放 `_pipe`；底层连接归 `ChannelServer` / `ChannelClient` 所有。

### 3. 握手在泵启动之前完成

```csharp
var ssl = new SslStream(new SslStreamDuplexPipe(inner), leaveInnerStreamOpen: false);

using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
handshakeCts.CancelAfter(options.HandshakeTimeout);

try
{
    if (isServer)
        await ssl.AuthenticateAsServerAsync(options.ServerOptions!, handshakeCts.Token)
            .ConfigureAwait(false);
    else
        await ssl.AuthenticateAsClientAsync(options.ClientOptions!, handshakeCts.Token)
            .ConfigureAwait(false);
}
catch
{
    await ssl.DisposeAsync().ConfigureAwait(false);
    throw;
}
```

- 握手成功后才创建 `TlsDuplexPipe` 并启动读/写泵；失败时不会向管线暴露任何可用传输。
- `HandshakeTimeout` 用 linked CTS 的 `CancelAfter` 实现；外部取消与超时都表现为 `OperationCanceledException`，调用方可从异常的 `CancellationToken` 区分，也可直接把它当作连接失败。
- 证书拒绝通常是 `AuthenticationException`；明文字节进入 TLS 端口通常是 `AuthenticationException` 或 `IOException`。适配器不把这些异常转换成“明文继续”。
- 不使用 `SslStream.AuthenticateAsServer` 同步版本，避免阻塞线程池线程。

### 4. 读/写泵、刷新与半关闭

- 读泵：`SslStream.ReadAsync` → 解密输入 `Pipe.Writer` → `FlushAsync`；读到 0 或异常时用 `Complete(error)` 结束解密输入，让处理器看到 EOF 或异常。
- 写泵：解密输出 `Pipe.Reader.ReadAsync` → `SslStream.WriteAsync` → `FlushAsync`；`ReadResult.IsCompleted` 时先 `SslStream.ShutdownAsync()` 发送 `close_notify`，再完成底层 `inner.Output`，让 `SocketChannelConnection` 的发送循环走 FIN。
- 半关闭：底层输入 EOF 只完成解密输入，不立即中止写方向；处理器仍可写响应。写方向完成后才发送 `close_notify` 并结束底层输出。
- `Abort` 路径不保证刷新：如果底层连接已被 `ChannelContext.Abort` 中止，写泵的异常被观察后直接释放，不把未发送数据当作已送达。
- 释放顺序：先完成输出并等待写泵排空（有界等待），再完成解密输入、释放 `SslStream` 和两个 `Pipe`；不调用 `inner.Input.Complete` / `inner.Output.Dispose`，底层由框架收尾。
- `DisposeAsync` 幂等；重复调用等待同一个完成任务。

### 5. 本 change 不接管线、不接客户端、不做 ALPN

- `UseTls` 需要 `ChannelContext.UseTransport`、异常传播和 `finally` 释放，放在下一个 change；本 change 只保证适配器可以独立创建和释放。
- `ChannelTlsOptions.ClientOptions` 在本 change 只参与 `ValidateClient()`；客户端连接与 `AuthenticateAsClientAsync` 的调用点在 `primitives-client-1`。
- `SslServerAuthenticationOptions.ApplicationProtocols` 原样传给 `SslStream`，但本 change 不做“同端口按 ALPN 选协议”。ALPN 协商结果只作为 `SslStream` 的属性存在，协议分发留给 P2.5/P3 后续 change。

### 6. 测试证书在进程内生成

`TlsTestCertificates` 用 `CertificateRequest` + `SubjectAlternativeNameBuilder` 生成自签名证书：

```csharp
using var rsa = RSA.Create(2048);
var request = new CertificateRequest(
    "CN=localhost",
    rsa,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);

var san = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(IPAddress.Loopback);
request.CertificateExtensions.Add(san.Build());

using var certificate = request.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-5),
    DateTimeOffset.UtcNow.AddHours(1));
```

- 不把 `.pfx` 提交进仓库，避免密码和平台差异。
- 服务端持有证书直到服务器释放；客户端校验回调按指纹匹配测试证书，不用 `dangerous` 的全接受回调。
- 测试使用真实 loopback socket 和 internal `SocketChannelConnection`，覆盖 `Pipe` 背压与部分发送，而不是只测内存流。

## Risks / Trade-offs

- **`SslStream` 没有真正的半关闭** → 以 `ShutdownAsync` + 底层 FIN 为准；测试锁定“客户端半关闭后仍能收到响应”和“输出完成后对端读到 EOF”。不承诺 TLS 层的双向半关闭独立控制。
- **握手超时与外部取消都表现为 `OperationCanceledException`** → 文档说明两者都按连接失败处理；需要区分的调用方检查 `CancellationToken` 或在外层记录原因。
- **`SslStreamDuplexPipe` 每次写都 flush 可能增加小写入开销** → 握手阶段必须如此；业务阶段由 `TlsDuplexPipe` 的写泵按 `Pipe` 的 `FlushAsync` 聚合，不直接暴露 `SslStreamDuplexPipe` 给业务。
- **大消息在 TLS 下多一次拷贝** → `SslStream` 本身需要明文缓冲区；测试用 1 MiB 数据锁定顺序与背压，暂不为零拷贝做自定义 BIO。
- **证书/协议差异导致测试在旧 Windows 上失败** → 测试只使用 TLS 1.2/1.3 的默认协商，不强制套件；证书用 RSA 2048 + SHA-256。
- **适配器释放顺序错误会导致死锁** → 释放时先完成输出、再有界等待写泵，最后释放 `SslStream`；测试覆盖正常关闭、重复释放和中止。
- **本 change 没有管线入口** → 适配器暂时只能被测试或后续 change 使用；这是刻意的分层，避免把 TLS 策略塞进传输内核。

## Migration Plan

1. 新增 `ChannelTlsOptions` 与校验测试，先锁定默认值、服务端证书与客户端 `TargetHost` 规则。
2. 新增 `TlsTestCertificates`，再实现 `SslStreamDuplexPipe`；用内存 `Pipe` 验证 `Stream` 适配器的读写与不释放底层。
3. 实现 `TlsDuplexPipe.CreateAsync` 的握手与读泵，跑通服务端/客户端握手。
4. 实现写泵、`ShutdownAsync`、半关闭与 `DisposeAsync`，补齐关闭/刷新测试。
5. 更新 Primitives README 的 TLS 章节，说明本阶段只有适配器，没有 `UseTls`。
6. 跑 Primitives 构建与全量测试；若失败，回滚新增文件和 README 即可，现有传输代码不变。

## Open Questions

无。`UseTls`、客户端 TLS、ALPN 和多证书选择明确留给后续 change。
