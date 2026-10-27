## Context

P1 的服务端已经把 socket 字节包装成 `IChannelConnection.Transport`，并交给 `ChannelContext`；P2 的 `IChannelSession<T>` 只消费 `ChannelContext.Reader/Writer`，不关心底层是 TCP 还是 TLS。设计文档 `2026-10-20-primitives-socket-transport-design.md` 在 §5.7 定义了客户端形态：连接级语义、`ConnectAsync` 幂等、`CloseAsync` 终态、消息收发走 `Connection.AsSession<T>()`。

当前仓库只有 internal `ChannelClientBase` 空壳，它继承 `IChannelServer`，没有 socket、没有状态、没有关闭语义，也没有任何引用。本 change 先交付单连接客户端；重连、连接池和 batch 分别由 `primitives-client-2`、`primitives-client-3` 叠加。客户端 TLS 依赖 `primitives-tls-transport-1` 的 `TlsDuplexPipe`，测试依赖 `primitives-server-composition-2` 的 sealed `ChannelServer` 与迁移后的 `TestServer`。

## Goals / Non-Goals

**Goals:**

- 提供 public `IChannelClient`、`ChannelClient`、`ChannelConnectionState`、`ChannelStateChangedEventArgs`、`ChannelConnectionFailure`。
- 提供 public `ChannelClientOptions`（`Microsoft.Extensions.Options`），覆盖端点、连接超时、keep-alive、TLS 与 `TimeProvider`。
- 连接成功后暴露一个 `ChannelContext`，其 `Reader`/`Writer` 是 TCP 或 TLS 解密后的字节管道；客户端不额外实现 `SendAsync` / `ReceiveAsync`。
- `ConnectAsync` 并发幂等；`CloseAsync` / `DisposeAsync` 到达终态 `Closed`，旧连接失效。
- 连接失败可分类（拒绝、超时、TLS、取消、配置），为 P4 重连提供 `IsRetryable`。
- 用真实 loopback socket 覆盖明文、TLS、失败、关闭和远端断开。

**Non-Goals:**

- 不实现自动重连、退避、`Reconnecting` 的实际使用；该状态只预留。
- 不实现连接池、租借超时、健康检查。
- 不实现批量写入或任何 session/codec/framer 逻辑；消息层通过 `Connection.AsSession<T>()` 在 P2 之后接入。
- 不提供多 `EndPoint` 故障转移、Unix socket 专用配置、代理或 HTTP 升级。
- 不在客户端启动后台读循环；读取由调用方通过 `Connection.Reader` 或 session 驱动。
- 不改变服务端 `IChannelServer`、`ChannelContext`、`IChannelConnection` 的公开契约。

## Decisions

### 1. 只保留一个具体 `ChannelClient`，删除空壳基类

设计文档曾列出 abstract `ChannelClientBase` + sealed `ChannelClient`。本 change 只交付 sealed `ChannelClient`，并删除 internal `ChannelClientBase.cs`：

- 当前基类是 `IChannelServer` 的错误实现，仓库内无引用，保留它只会让“客户端是服务器”的误解继续存在。
- v1 没有第二个客户端实现，抽象基类只会冻结 protected 构造与字段布局；需要扩展点时再引入 public 抽象层是兼容的。
- `IChannelClient` 仍是唯一公开契约，调用方不依赖具体类型；测试可以直接 `new ChannelClient(options)`。

### 2. 复用 internal `SocketChannelConnection` 与 `ChannelContext`

`ChannelClient` 自己创建 `Socket`，然后按服务端相同的方式构造连接：

```csharp
var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, protocol);
await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

var connection = new SocketChannelConnection(
    socket,
    new ChannelServerOptions { EndPoint = endPoint },
    logger: null);

var context = new ChannelContext(
    Guid.NewGuid().ToString("N"),
    connection,
    EmptyServiceProvider.Instance);
```

- 不复制 socket 读写循环。`SocketChannelConnection` 已经处理 `Pipe` 背压、发送排空、半关闭、RST 和 `Abort`。
- `ChannelServerOptions` 只用于复用 `MaxReadBufferSize` / `MaxWriteBufferSize` 默认值；客户端不暴露这两个旋钮，避免 P4 出现第二套传输配置。
- `ChannelContext` 的 `ConnectionId` 每次连接重新生成；重连（后续 change）产生新 context，旧 context 不复活。
- `EmptyServiceProvider.Instance` 是现有 internal 单例；客户端没有 per-connection DI scope。需要服务的调用方在创建 session 前自行组合，或等待后续客户端 DI change。
- 客户端不注册 handler，也不启动 `ChannelServer`；`ChannelContext` 只作为连接视图。

### 3. 状态机与并发

```csharp
public enum ChannelConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting, // P4 预留，本 change 不进入
    Closed,
}
```

- 所有状态与连接字段由同一个 `_gate` 保护；`StateChanged` 在锁外同步触发，保证订阅方看到的顺序与状态字段一致。
- `ConnectAsync` 在 `Connected` 时直接完成；在 `Connecting` 时返回同一个尝试任务；在 `Closed` 时抛 `InvalidOperationException`（`DisposeAsync` 之后允许 `ObjectDisposedException`）。
- 首次调用者的 `CancellationToken` 控制整个连接尝试；后续并发调用只取消自己的等待，不取消共享尝试。这样不会因为一个等待者退出而让其他调用方莫名失败。
- 事件订阅方抛异常不得破坏状态机；`StateChanged` 调用包在 `try/catch` 中，异常不向连接路径传播。
- 状态迁移只允许：`Disconnected → Connecting → Connected`、`Connecting → Disconnected`、`Connected → Disconnected`、任意非终态 → `Closed`。`Closed` 之后静默忽略迁移。

### 4. 连接尝试、超时与失败分类

```csharp
public sealed class ChannelConnectionFailure
{
    public ChannelConnectionFailureKind Kind { get; }
    public Exception Exception { get; }
    public bool IsRetryable => Kind is
        ChannelConnectionFailureKind.ConnectionRefused or
        ChannelConnectionFailureKind.TimedOut or
        ChannelConnectionFailureKind.HostUnreachable or
        ChannelConnectionFailureKind.Network;
}
```

- `ConnectTimeout` 覆盖 TCP connect 与可选的 TLS 客户端握手；用 linked CTS 的 `CancelAfter` 实现，不依赖 `Socket.ConnectAsync` 自带超时。
- 超时映射为 `TimeoutException`，`Kind = TimedOut`；调用方取消保持 `OperationCanceledException`，`Kind = Canceled`，且 `IsRetryable = false`。
- `SocketException` 按 `SocketErrorCode` 细分为 `ConnectionRefused`、`TimedOut`、`HostUnreachable`、`Network`；其余 socket/IO 错误归为 `Network`。
- `AuthenticationException` 归为 `Authentication`，不重试；选项校验失败在构造时抛 `ArgumentException`，不进入状态机。
- 失败时先清理半成品 socket/connection/TLS，再发布 `Disconnected` 与 `Failure`，最后原样抛出异常。不会把 `ChannelContext` 暴露给调用方。
- 本 change 不做自动重试；`ConnectAsync` 只执行一次尝试。P4 在失败分类之上叠加 `ChannelReconnectPolicy`。

### 5. TLS 在暴露 `Connection` 之前完成

- `ChannelClientOptions.Tls` 非空时，在 `SocketChannelConnection` 建立后调用 `TlsDuplexPipe.CreateAsync(connection.Transport, tls, isServer: false, ct)`；握手失败立即释放连接，不发布 `Connected`。
- 握手成功后先创建 `ChannelContext`，再调用 `context.UseTransport(tlsPipe)`；`ChannelClient` 单独持有 `TlsDuplexPipe` 引用。
- `Connection` 的 `Reader`/`Writer` 因此直接是解密后的管道；调用方和 P2 session 不需要知道 TLS 存在。
- 释放顺序固定为：`TlsDuplexPipe.DisposeAsync()`（排空输出并发送 `close_notify`）→ `ChannelContext.DisposeAsync()`（完成底层输出、释放 socket）。`ChannelContext` 不知道包装层，不能替客户端释放 TLS。
- 未配置 TLS 时使用明文 `connection.Transport`，不做协议探测，也不在握手失败后回退明文。

### 6. 用 internal `Completion` 观察远端结束

`SocketChannelConnection.Termination` 只在错误或主动中止时完成；对端正常关闭发送方向时，接收循环只是完成输入管道，`Termination` 仍保持未完成。客户端需要一个统一的“这条连接不会再收到字节”信号：

```csharp
internal interface IChannelConnection : IAsyncDisposable
{
    // 现有成员...
    Task Completion { get; }
}
```

- `Completion` 在接收循环退出（EOF、错误、`Abort`、`Dispose`）时完成一次，不携带异常；错误原因仍从 `Termination` 读取。
- `ChannelClient` 在 `Connected` 后启动一个轻量 watcher `await connection.Completion`。若当前 context 仍是活动连接，则先释放 TLS/context，再把状态迁移到 `Disconnected`；P4 在这里改为 `Reconnecting`。
- 客户端把远端输入 EOF 视为连接结束，不支持“只关闭服务端发送方向、客户端继续写”的半关闭用法；这是连接级客户端的刻意取舍。
- watcher 不阻塞 `ConnectAsync`，异常全部观察；释放与状态迁移在同一个串行化路径内完成，避免与 `CloseAsync` 竞争。

### 7. 关闭与释放

- `CloseAsync` 先把状态置为 `Closed`（只触发一次事件），取消进行中的连接尝试，然后按 TLS → context 的顺序释放；输出排空受 5 秒上限约束，超时或调用方取消则 `Abort` 后继续收尾。
- `CloseAsync` 幂等：重复调用返回同一个关闭任务；关闭后 `State` 恒为 `Closed`，`Connection` 为 null。
- `DisposeAsync` 幂等，内部调用同一关闭路径；它不重复触发 `StateChanged`。
- 已捕获的旧 `ChannelContext` 在关闭/远端断开时被释放：`ConnectionClosed` 已触发，后续读写抛 `ObjectDisposedException` 或 `InvalidOperationException`。`ChannelClient.Connection` 在离开 `Connected` 的瞬间即为 null。
- 关闭期间不缓冲、不重放未发送消息；`Pipe` 中已提交的数据按底层排空规则处理，未提交的写入由调用方负责。

## Risks / Trade-offs

- **`ConnectAsync` 首次调用者的取消会影响所有并发等待者** → 文档写明共享尝试语义；后续调用者可以用自己的 token 只取消等待。若需要“每个调用者独立取消且尝试继续”，放在 P4 的 manager 层解决。
- **事件同步触发可能阻塞连接路径** → 订阅方必须快速返回；框架吞掉订阅方异常，避免状态机被破坏，但调用方无法从 `ConnectAsync` 看到订阅方错误。
- **`ChannelContext` 不知道 TLS 包装层** → 客户端必须严格按 TLS → context 顺序释放；测试锁定关闭时数据排空与 `close_notify`。这是复用现有 context 的代价。
- **远端半关闭被当作断开** → 连接级语义优先于半关闭复用；需要半关闭的协议应在消息层显式建模，而不是依赖客户端状态。
- **`Completion` 是新增 internal 成员** → 只有 `SocketChannelConnection` 一个实现，修改面可控；不进入 public API。
- **连接超时同时覆盖 TLS 握手** → 如果 `ChannelTlsOptions.HandshakeTimeout` 更短，以先到者为准；两个超时都表现为连接失败，调用方从异常类型区分。
- **客户端没有 logger** → 本 change 不引入日志依赖；失败通过异常与 `StateChanged` 暴露。后续需要诊断时再增加 `ILoggerFactory` 可选参数。
- **`EmptyServiceProvider` 没有业务服务** → 客户端 session 只能使用显式传入的 framer/codec；需要 DI 的 handler 风格客户端留给后续 change。

## Migration Plan

1. 增加状态、事件、失败分类与 `ChannelClientOptions`，先用单元测试锁定默认值、校验和克隆语义。
2. 给 `IChannelConnection` / `SocketChannelConnection` 增加 `Completion`，用现有传输测试确认不影响服务端关闭。
3. 实现 `ChannelClient` 的 socket 连接、状态迁移、失败清理与 `ConnectAsync` 并发合并；跑通明文回显。
4. 接入 `TlsDuplexPipe`，补 TLS 回显与证书失败测试。
5. 实现 `CloseAsync` / `DisposeAsync`、watcher、旧连接失效测试。
6. 删除 `ChannelClientBase.cs`，更新 Primitives README；跑构建、全量测试与 OpenSpec 校验。
7. 若需要回滚：删除新增 Client 目录与 Options 文件、恢复 `ChannelClientBase.cs`、移除 `Completion` 成员即可；现有服务端代码不受影响。

## Open Questions

无。重连、连接池、batch、多 EndPoint 与客户端 DI 明确留给后续 change。
