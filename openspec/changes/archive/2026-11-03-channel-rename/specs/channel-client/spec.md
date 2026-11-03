# Spec Delta

## MODIFIED Requirements

### Requirement: 客户端契约与状态

The implementation SHALL provide a public `IChannelClient : IAsyncDisposable` in `Galosys.Foundation.Channel` with `ChannelConnectionState State`, `ChannelContext? Connection`, `event EventHandler<ChannelStateChangedEventArgs>? StateChanged`, `Task ConnectAsync(CancellationToken ct)`, and `Task CloseAsync(CancellationToken ct)`. It SHALL also provide a public sealed `ChannelClient` implementing the interface. `Connection` MUST be non-null only while `State` is `Connected`. The state set MUST be `Disconnected`, `Connecting`, `Connected`, `Reconnecting`, and `Closed`. `Reconnecting` MUST be entered only after a retryable failure when `ChannelClientOptions.Reconnect` is configured, and `Connection` MUST be null while in that state. Every actual state change MUST raise `StateChanged` exactly once with the previous state, the new state, and an optional `ChannelConnectionFailure`. `Closed` MUST be terminal, `Connection` MUST become null when leaving `Connected`, and no transition out of `Closed` may occur.

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

#### Scenario: 连接断开后清空连接视图

- **WHEN** 未配置重连策略（或失败不可重试）时，已连接的客户端遇到远端关闭并迁移到 `Disconnected`
- **THEN** `StateChanged` 报告 `Connected → Disconnected`
- **AND** `Connection` 变为 null
- **AND** 之前捕获的 `ChannelContext` 的 `ConnectionClosed` 已触发

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
