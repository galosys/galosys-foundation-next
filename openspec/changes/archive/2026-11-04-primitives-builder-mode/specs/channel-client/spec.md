# Spec Delta

## MODIFIED Requirements

### Requirement: 客户端配置

The implementation SHALL provide a public sealed `ChannelClientOptions` in `Microsoft.Extensions.Options` with `EndPoint EndPoint`, `TimeSpan ConnectTimeout` defaulting to 10 seconds, `ChannelKeepAliveOptions? KeepAlive`, `ChannelTlsOptions? Tls`, `TimeProvider TimeProvider` defaulting to `TimeProvider.System`, and `ChannelReconnectPolicy? Reconnect` defaulting to null. `ChannelClientOptions` MUST validate that `EndPoint` is non-null, `ConnectTimeout` is greater than zero and at most one day, `TimeProvider` is non-null, keep-alive values follow the server rules, `Tls`, when present, passes client-side TLS validation, and `Reconnect`, when present, passes reconnect policy validation. The build path MUST validate and clone the options, including a separate `ChannelReconnectPolicy` instance, so later mutations of the caller's instances do not affect a running client. Invalid configuration MUST throw `ArgumentException` before any socket is created.

#### Scenario: 默认值

- **WHEN** 创建 `ChannelClientOptions` 并只设置 `EndPoint`
- **THEN** `ConnectTimeout` 为 10 秒
- **AND** `TimeProvider` 为 `TimeProvider.System`
- **AND** `KeepAlive`、`Tls` 与 `Reconnect` 为 null

#### Scenario: 缺少端点

- **WHEN** `EndPoint` 为 null 时通过构建器创建客户端
- **THEN** 抛出 `ArgumentException`
- **AND** 不创建 socket、不进入 `Connecting`

#### Scenario: 非法连接超时

- **WHEN** `ConnectTimeout` 为 0、负值或超过 1 天
- **THEN** 抛出 `ArgumentException`

#### Scenario: 非法 keep-alive

- **WHEN** `KeepAlive.Enabled` 为 true 且 `KeepAliveTime` 或 `KeepAliveInterval` 小于 1 秒，或 `KeepAliveRetryCount` 为负数
- **THEN** 抛出 `ArgumentException`

#### Scenario: 非法重连策略

- **WHEN** `Reconnect.MaxAttempts` 为负数、`InitialDelay` 非正、`MaxDelay` 小于 `InitialDelay`、`BackoffMultiplier` 小于 1，或 `JitterFactor` 不在 `[0, 1]`
- **THEN** 抛出 `ArgumentException`

#### Scenario: 客户端 TLS 配置校验

- **WHEN** `Tls` 非空但 `ClientOptions.TargetHost` 为空
- **THEN** 抛出 `ArgumentException`
- **AND** 不会尝试 socket 连接或 TLS 握手

#### Scenario: 选项克隆隔离

- **WHEN** 构建客户端后修改原 `ChannelClientOptions.Reconnect.InitialDelay` 或把 `Reconnect` 置为 null
- **THEN** 运行中的客户端继续使用构建时的策略
- **AND** 修改不会影响后续连接或重连尝试
