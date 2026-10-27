## ADDED Requirements

### Requirement: 客户端契约与状态

The implementation SHALL provide a public `IChannelClient : IAsyncDisposable` in `Microsoft.Extensions.Hosting.Net` with `ChannelConnectionState State`, `ChannelContext? Connection`, `event EventHandler<ChannelStateChangedEventArgs>? StateChanged`, `Task ConnectAsync(CancellationToken ct)`, and `Task CloseAsync(CancellationToken ct)`. It SHALL also provide a public sealed `ChannelClient` implementing the interface. `Connection` MUST be non-null only while `State` is `Connected`. The state set MUST be `Disconnected`, `Connecting`, `Connected`, `Reconnecting`, and `Closed`; `Reconnecting` is reserved in this change and MUST NOT be entered. Every actual state change MUST raise `StateChanged` exactly once with the previous state, the new state, and an optional `ChannelConnectionFailure`. `Closed` MUST be terminal, `Connection` MUST become null when leaving `Connected`, and no transition out of `Closed` may occur.

#### Scenario: 初始状态

- **WHEN** 创建 `ChannelClient` 且尚未调用 `ConnectAsync`
- **THEN** `State` 为 `Disconnected`
- **AND** `Connection` 为 null
- **AND** 没有触发 `StateChanged`

#### Scenario: 连接成功的事件顺序

- **WHEN** `ConnectAsync` 成功
- **THEN** `StateChanged` 依次报告 `Disconnected → Connecting` 与 `Connecting → Connected`
- **AND** 每次事件的 `OldState` / `NewState` 与实际迁移一致
- **AND** `Connection` 只在第二个事件之后非 null

#### Scenario: 连接断开后清空连接视图

- **WHEN** 已连接的客户端遇到远端关闭并迁移到 `Disconnected`
- **THEN** `StateChanged` 报告 `Connected → Disconnected`
- **AND** `Connection` 变为 null
- **AND** 之前捕获的 `ChannelContext` 的 `ConnectionClosed` 已触发

#### Scenario: Closed 是终态

- **WHEN** 客户端已进入 `Closed` 后再次调用 `ConnectAsync`
- **THEN** 抛出 `InvalidOperationException`（`DisposeAsync` 之后允许 `ObjectDisposedException`）
- **AND** `State` 保持 `Closed`
- **AND** 不再触发 `StateChanged`

### Requirement: 单连接建立

The implementation SHALL create exactly one socket-based connection per successful `ConnectAsync`, using `ChannelClientOptions.EndPoint`, the configured `ConnectTimeout`, TCP no-delay, and optional keep-alive. The attempt MUST include the optional client TLS handshake before any `ChannelContext` is exposed. Concurrent `ConnectAsync` calls made before completion MUST share one attempt; a call while `Connected` MUST complete without opening another connection. A successful connect MUST create a new `ChannelContext` with a new `ConnectionId`, and each attempt MUST NOT be retried in this change. On failure, the client MUST clean up partial resources, return to `Disconnected`, publish one `ChannelConnectionFailure`, and rethrow the original exception. Caller cancellation MUST surface as `OperationCanceledException` with a non-retryable failure and MUST NOT leave a connected or half-initialized client.

#### Scenario: 明文 loopback 回显

- **WHEN** 服务端使用 `ChannelServer` + `Run(echoHandler)` 启动，客户端配置该监听端点并调用 `ConnectAsync`
- **THEN** 连接成功且 `State` 为 `Connected`
- **AND** 通过 `Connection.Writer` 写入的字节按顺序从服务端回显到 `Connection.Reader`

#### Scenario: 并发 ConnectAsync 共享一次连接

- **WHEN** 两个任务同时对同一客户端调用 `ConnectAsync`
- **THEN** 只建立一个 socket 连接
- **AND** 两个任务都成功返回
- **AND** 两个任务看到同一个 `Connection` 实例

#### Scenario: 已连接时再次 ConnectAsync

- **WHEN** `State` 已为 `Connected` 时再次调用 `ConnectAsync`
- **THEN** 方法立即成功返回
- **AND** 不创建新 socket、不更换 `ConnectionId`
- **AND** 不触发额外的 `StateChanged`

#### Scenario: 连接被拒绝

- **WHEN** 目标端点没有监听器，`ConnectAsync` 收到 `SocketException`（`ConnectionRefused`）
- **THEN** 抛出原始 `SocketException`
- **AND** 状态回到 `Disconnected`
- **AND** `StateChanged` 携带 `ChannelConnectionFailureKind.ConnectionRefused` 且 `IsRetryable` 为 true

#### Scenario: 连接超时

- **WHEN** 目标地址在 `ConnectTimeout` 内未完成 TCP 连接或 TLS 握手
- **THEN** `ConnectAsync` 抛出 `TimeoutException`
- **AND** 失败分类为 `TimedOut` 且 `IsRetryable` 为 true
- **AND** 半成品 socket 与 TLS 包装层被释放

#### Scenario: 调用方取消连接

- **WHEN** 首次调用者的 `CancellationToken` 在连接完成前取消
- **THEN** `ConnectAsync` 抛出 `OperationCanceledException`
- **AND** 状态为 `Disconnected`
- **AND** 失败分类为 `Canceled` 且 `IsRetryable` 为 false

#### Scenario: 新连接产生新 ConnectionId

- **WHEN** 客户端在关闭后重新创建实例并成功连接，或在同一实例上从失败恢复到 `Disconnected` 后再次成功连接
- **THEN** 新的 `Connection.ConnectionId` 与之前的 `ConnectionId` 不同
- **AND** 之前的 `ChannelContext` 保持关闭且不能被复用

### Requirement: 客户端关闭与释放

`CloseAsync` MUST transition the client to `Closed` exactly once, cancel an in-flight connection attempt, flush committed output with a bounded drain, dispose the TLS wrapper before the underlying `ChannelContext`, set `Connection` to null, and make all previously captured contexts unusable. `CloseAsync` MUST be idempotent and MUST NOT throw when the client is already closed. Cancellation of `CloseAsync` MUST abort the remaining drain, still reach `Closed`, and complete without leaving the socket open. `DisposeAsync` MUST be idempotent, use the same shutdown path, and release the client's resources. After `Closed`, no reconnect, connect, or state event may occur.

#### Scenario: 优雅关闭排空已提交输出

- **WHEN** 客户端写入一段数据并调用 `CloseAsync`
- **THEN** 服务端在 EOF 前收到完整数据
- **AND** 客户端 `State` 为 `Closed`
- **AND** `Connection` 为 null

#### Scenario: 关闭打断连接中的尝试

- **WHEN** `ConnectAsync` 仍处于 `Connecting` 时调用 `CloseAsync`
- **THEN** 连接尝试被取消
- **AND** 状态最终为 `Closed`，而不是 `Disconnected`
- **AND** 不会留下已连接但未暴露的 socket

#### Scenario: 重复关闭

- **WHEN** 连续调用两次 `CloseAsync` 或 `DisposeAsync`
- **THEN** 第二次调用正常完成
- **AND** 不抛异常、不重复释放、不重复触发 `StateChanged`

#### Scenario: 旧连接失效

- **WHEN** 客户端关闭后使用之前捕获的 `ChannelContext.Reader` 或 `Writer`
- **THEN** 读取或写入抛出 `ObjectDisposedException` 或 `InvalidOperationException`
- **AND** `ConnectionClosed` 已在此之前触发
- **AND** 不会访问已释放的 socket

#### Scenario: 关闭取消仍有界收尾

- **WHEN** `CloseAsync` 的 `CancellationToken` 在输出排空期间取消
- **THEN** 客户端中止剩余排空并释放连接
- **AND** 状态仍为 `Closed`
- **AND** `CloseAsync` 在有限时间内完成

### Requirement: 客户端配置

The implementation SHALL provide a public sealed `ChannelClientOptions` in `Microsoft.Extensions.Options` with `EndPoint EndPoint`, `TimeSpan ConnectTimeout` defaulting to 10 seconds, `ChannelKeepAliveOptions? KeepAlive`, `ChannelTlsOptions? Tls`, and `TimeProvider TimeProvider` defaulting to `TimeProvider.System`. `ChannelClientOptions` MUST validate that `EndPoint` is non-null, `ConnectTimeout` is greater than zero and at most one day, `TimeProvider` is non-null, keep-alive values follow the server rules, and `Tls`, when present, passes client-side TLS validation. `ChannelClient` MUST validate and clone the options in its constructor, so later mutations of the caller's instance do not affect a running client. Invalid configuration MUST throw `ArgumentException` before any socket is created.

#### Scenario: 默认值

- **WHEN** 创建 `ChannelClientOptions` 并只设置 `EndPoint`
- **THEN** `ConnectTimeout` 为 10 秒
- **AND** `TimeProvider` 为 `TimeProvider.System`
- **AND** `KeepAlive` 与 `Tls` 为 null

#### Scenario: 缺少端点

- **WHEN** `EndPoint` 为 null 时构造 `ChannelClient`
- **THEN** 构造函数抛出 `ArgumentException`
- **AND** 不创建 socket、不进入 `Connecting`

#### Scenario: 非法连接超时

- **WHEN** `ConnectTimeout` 为 0、负值或超过 1 天
- **THEN** 构造函数抛出 `ArgumentException`

#### Scenario: 非法 keep-alive

- **WHEN** `KeepAlive.Enabled` 为 true 且 `KeepAliveTime` 或 `KeepAliveInterval` 小于 1 秒，或 `KeepAliveRetryCount` 为负数
- **THEN** 构造函数抛出 `ArgumentException`

#### Scenario: 客户端 TLS 配置校验

- **WHEN** `Tls` 非空但 `ClientOptions.TargetHost` 为空
- **THEN** 构造函数抛出 `ArgumentException`
- **AND** 不会尝试 socket 连接或 TLS 握手

#### Scenario: 选项克隆隔离

- **WHEN** 构造 `ChannelClient` 后修改原 `ChannelClientOptions.ConnectTimeout` 或 `KeepAlive`
- **THEN** 运行中的客户端继续使用构造时的值
- **AND** 修改不会影响后续连接尝试
