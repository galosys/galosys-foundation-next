## MODIFIED Requirements

### Requirement: 客户端契约与状态

The implementation SHALL provide a public `IChannelClient : IAsyncDisposable` in `Microsoft.Extensions.Hosting.Net` with `ChannelConnectionState State`, `ChannelContext? Connection`, `event EventHandler<ChannelStateChangedEventArgs>? StateChanged`, `Task ConnectAsync(CancellationToken ct)`, and `Task CloseAsync(CancellationToken ct)`. It SHALL also provide a public sealed `ChannelClient` implementing the interface. `Connection` MUST be non-null only while `State` is `Connected`. The state set MUST be `Disconnected`, `Connecting`, `Connected`, `Reconnecting`, and `Closed`. `Reconnecting` MUST be entered only after a retryable failure when `ChannelClientOptions.Reconnect` is configured, and `Connection` MUST be null while in that state. Every actual state change MUST raise `StateChanged` exactly once with the previous state, the new state, and an optional `ChannelConnectionFailure`. `Closed` MUST be terminal, `Connection` MUST become null when leaving `Connected`, and no transition out of `Closed` may occur.

#### Scenario: 初始状态

- **WHEN** 创建 `ChannelClient` 且尚未调用 `ConnectAsync`
- **THEN** `State` 为 `Disconnected`
- **AND** `Connection` 为 null
- **AND** 没有触发 `StateChanged`

#### Scenario: 连接成功的事件顺序

- **WHEN** 未配置重连策略且 `ConnectAsync` 成功
- **THEN** `StateChanged` 依次报告 `Disconnected → Connecting` 与 `Connecting → Connected`
- **AND** 每次事件的 `OldState` / `NewState` 与实际迁移一致
- **AND** `Connection` 只在第二个事件之后非 null

#### Scenario: 重连时连接视图为空

- **WHEN** 已连接的客户端因可重试失败进入 `Reconnecting`
- **THEN** `StateChanged` 报告 `Connected → Reconnecting`
- **AND** `Connection` 为 null
- **AND** 之前捕获的 `ChannelContext` 的 `ConnectionClosed` 已触发

#### Scenario: 重连成功回到 Connected

- **WHEN** 重连尝试成功
- **THEN** `StateChanged` 报告 `Reconnecting → Connected`
- **AND** 新的 `Connection` 非 null 且 `ConnectionId` 与旧连接不同

#### Scenario: Closed 是终态

- **WHEN** 客户端已进入 `Closed` 后再次调用 `ConnectAsync`
- **THEN** 抛出 `InvalidOperationException`（`DisposeAsync` 之后允许 `ObjectDisposedException`）
- **AND** `State` 保持 `Closed`
- **AND** 不再触发 `StateChanged`

### Requirement: 单连接建立

The implementation SHALL create one socket-based connection per attempt, using `ChannelClientOptions.EndPoint`, the configured `ConnectTimeout`, TCP no-delay, and optional keep-alive. The attempt MUST include the optional client TLS handshake before any `ChannelContext` is exposed. Concurrent `ConnectAsync` calls MUST share one operation; a call while `Connected` MUST complete without opening another connection. A successful connect MUST create a new `ChannelContext` with a new `ConnectionId`. When `ChannelClientOptions.Reconnect` is null, a failure MUST NOT be retried and `ConnectAsync` MUST surface the original exception. When `Reconnect` is configured, `ConnectAsync` MUST retry retryable failures according to the reconnect policy and MUST complete only after a successful connection, a terminal failure, or cancellation. On every failed attempt, the client MUST clean up partial resources and publish the failure. Caller cancellation MUST surface as `OperationCanceledException` with a non-retryable failure and MUST NOT leave a connected or half-initialized client.

#### Scenario: 明文 loopback 回显

- **WHEN** 服务端使用 `ChannelServer` + `Run(echoHandler)` 启动，客户端配置该监听端点并调用 `ConnectAsync`
- **THEN** 连接成功且 `State` 为 `Connected`
- **AND** 通过 `Connection.Writer` 写入的字节按顺序从服务端回显到 `Connection.Reader`

#### Scenario: 并发 ConnectAsync 共享一次操作

- **WHEN** 两个任务同时对同一客户端调用 `ConnectAsync`
- **THEN** 只运行一条连接/重试操作
- **AND** 两个任务在操作成功时都成功返回
- **AND** 两个任务看到同一个 `Connection` 实例

#### Scenario: 已连接时再次 ConnectAsync

- **WHEN** `State` 已为 `Connected` 时再次调用 `ConnectAsync`
- **THEN** 方法立即成功返回
- **AND** 不创建新 socket、不更换 `ConnectionId`
- **AND** 不触发额外的 `StateChanged`

#### Scenario: 无重连策略时连接被拒绝

- **WHEN** `Reconnect` 为 null，目标端点没有监听器，`ConnectAsync` 收到 `SocketException`（`ConnectionRefused`）
- **THEN** 抛出原始 `SocketException`
- **AND** 状态回到 `Disconnected`
- **AND** `StateChanged` 携带 `ChannelConnectionFailureKind.ConnectionRefused` 且 `IsRetryable` 为 true
- **AND** 不进入 `Reconnecting`

#### Scenario: 有重连策略时首次连接重试成功

- **WHEN** 首次连接被拒绝、`Reconnect` 配置为可重试，随后服务端开始监听且退避时间到达
- **THEN** 客户端进入 `Reconnecting`
- **AND** 重试成功并进入 `Connected`
- **AND** `ConnectAsync` 在成功后才返回

#### Scenario: 调用方取消连接

- **WHEN** 首次调用者的 `CancellationToken` 在连接或退避期间取消
- **THEN** `ConnectAsync` 抛出 `OperationCanceledException`
- **AND** 状态为 `Disconnected`
- **AND** 失败分类为 `Canceled` 且 `IsRetryable` 为 false

#### Scenario: 新连接产生新 ConnectionId

- **WHEN** 客户端在关闭后重新创建实例并成功连接，或重连成功
- **THEN** 新的 `Connection.ConnectionId` 与之前的 `ConnectionId` 不同
- **AND** 之前的 `ChannelContext` 保持关闭且不能被复用

### Requirement: 客户端配置

The implementation SHALL provide a public sealed `ChannelClientOptions` in `Microsoft.Extensions.Options` with `EndPoint EndPoint`, `TimeSpan ConnectTimeout` defaulting to 10 seconds, `ChannelKeepAliveOptions? KeepAlive`, `ChannelTlsOptions? Tls`, `TimeProvider TimeProvider` defaulting to `TimeProvider.System`, and `ChannelReconnectPolicy? Reconnect` defaulting to null. `ChannelClientOptions` MUST validate that `EndPoint` is non-null, `ConnectTimeout` is greater than zero and at most one day, `TimeProvider` is non-null, keep-alive values follow the server rules, `Tls`, when present, passes client-side TLS validation, and `Reconnect`, when present, passes reconnect policy validation. `ChannelClient` MUST validate and clone the options in its constructor, including a separate `ChannelReconnectPolicy` instance, so later mutations of the caller's instances do not affect a running client. Invalid configuration MUST throw `ArgumentException` before any socket is created.

#### Scenario: 默认值

- **WHEN** 创建 `ChannelClientOptions` 并只设置 `EndPoint`
- **THEN** `ConnectTimeout` 为 10 秒
- **AND** `TimeProvider` 为 `TimeProvider.System`
- **AND** `KeepAlive`、`Tls` 与 `Reconnect` 为 null

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

#### Scenario: 非法重连策略

- **WHEN** `Reconnect.MaxAttempts` 为负数、`InitialDelay` 非正、`MaxDelay` 小于 `InitialDelay`、`BackoffMultiplier` 小于 1，或 `JitterFactor` 不在 `[0, 1]`
- **THEN** 构造函数抛出 `ArgumentException`

#### Scenario: 客户端 TLS 配置校验

- **WHEN** `Tls` 非空但 `ClientOptions.TargetHost` 为空
- **THEN** 构造函数抛出 `ArgumentException`
- **AND** 不会尝试 socket 连接或 TLS 握手

#### Scenario: 选项克隆隔离

- **WHEN** 构造 `ChannelClient` 后修改原 `ChannelClientOptions.Reconnect.InitialDelay` 或把 `Reconnect` 置为 null
- **THEN** 运行中的客户端继续使用构造时的策略
- **AND** 修改不会影响后续连接或重连尝试

## ADDED Requirements

### Requirement: 自动重连与退避

The implementation SHALL reconnect after an established connection is lost when a `ChannelReconnectPolicy` is configured. On an unexpected `Connected` connection end, the client MUST dispose the old TLS wrapper and `ChannelContext`, publish `Reconnecting` with the last failure, and start a background retry operation. Each retry MUST create a new socket and `ChannelContext`; success MUST publish `Connected` with a new `ConnectionId`. Delays MUST start at `InitialDelay`, multiply by `BackoffMultiplier` after each failed attempt, cap at `MaxDelay`, and apply jitter in `[1 - JitterFactor, 1 + JitterFactor]`. Delays MUST use `TimeProvider` through `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`; bare `Task.Delay` MUST NOT be used for backoff. `MaxAttempts = 0` MUST mean unlimited retries; a positive value MUST limit the number of retries after the initial failure. A failure rejected by `ShouldRetry` or by the default classification MUST stop retrying and publish `Disconnected`. `CloseAsync` or `DisposeAsync` MUST cancel any delay or in-flight retry, keep `Closed`, and prevent any further connection attempt.

#### Scenario: 已连接后断线自动重连

- **WHEN** 服务端主动中止第一条连接，客户端配置了可重试策略，随后服务端恢复监听
- **THEN** 客户端从 `Connected` 进入 `Reconnecting`
- **AND** 在退避时间到达后建立新连接并进入 `Connected`
- **AND** 新连接的 `ConnectionId` 与旧连接不同

#### Scenario: 指数退避与上限

- **WHEN** 连续多次重连失败，`InitialDelay` 为 100 毫秒、`BackoffMultiplier` 为 2、`MaxDelay` 为 250 毫秒
- **THEN** 前三次等待分别为 100、200、250 毫秒
- **AND** 后续等待保持 250 毫秒，不再增长

#### Scenario: 尝试次数耗尽

- **WHEN** `MaxAttempts` 为 2 且两次重试都失败
- **THEN** `StateChanged` 最终报告 `Disconnected`
- **AND** 不再创建新的 socket
- **AND** 携带最后一次失败

#### Scenario: TLS 证书失败不重试

- **WHEN** 首次连接或重连时 TLS 证书校验失败
- **THEN** 失败分类为 `Authentication`
- **AND** 不进入 `Reconnecting`
- **AND** 状态回到 `Disconnected`

#### Scenario: ShouldRetry 覆盖默认分类

- **WHEN** `ShouldRetry` 对 `ConnectionRefused` 返回 false
- **THEN** 该失败不触发重连
- **AND** `ConnectAsync` 直接抛出原始异常
- **AND** 状态为 `Disconnected`

#### Scenario: 关闭打断退避

- **WHEN** 客户端处于 `Reconnecting` 且退避计时器尚未到期时调用 `CloseAsync`
- **THEN** 退避被取消，状态变为 `Closed`
- **AND** 即使随后推进假时钟，也不会再发起连接
- **AND** 不再触发 `StateChanged`

#### Scenario: 重连不缓冲消息

- **WHEN** 客户端在 `Reconnecting` 期间尝试通过旧 `Connection` 写入
- **THEN** 写入失败，不进入队列
- **AND** 重连成功后旧 `ChannelContext` 仍不可用
- **AND** 框架不重放任何断开期间的字节
