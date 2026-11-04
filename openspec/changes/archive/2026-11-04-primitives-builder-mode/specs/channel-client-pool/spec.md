# Spec Delta

## MODIFIED Requirements

### Requirement: 连接池工厂注入

`ChannelClientPool` MUST accept an optional `IChannelClientFactory` and MUST use `ChannelClientFactory` when none is supplied. Every client created by the pool MUST come from the configured factory. The factory MUST be selectable through the pool build path.

#### Scenario: 连接池使用默认工厂

- **WHEN** 构建连接池时未指定工厂
- **THEN** 池使用 `ChannelClientFactory` 创建客户端

#### Scenario: 连接池使用注入的工厂

- **WHEN** 构建连接池时指定自定义 `IChannelClientFactory`
- **THEN** 池创建的每个客户端都来自该工厂

### Requirement: 连接池配置

The implementation SHALL provide a public sealed `ChannelClientPoolOptions` in `Microsoft.Extensions.Options` with `ChannelClientOptions ClientOptions`, `int MaxSize` defaulting to 8, and `TimeSpan RentTimeout` defaulting to 30 seconds. `ClientOptions` MUST be non-null and valid; `MaxSize` MUST be greater than zero; `RentTimeout` MUST be greater than zero and at most one day. The build path MUST validate and clone the options so later caller mutations do not affect a running pool.

#### Scenario: 默认配置

- **WHEN** 创建 `ChannelClientPoolOptions`
- **THEN** `MaxSize` 为 8
- **AND** `RentTimeout` 为 30 秒
- **AND** `ClientOptions` 非 null

#### Scenario: 非法池配置

- **WHEN** `ClientOptions` 为 null 或无效、`MaxSize` 小于等于 0，或 `RentTimeout` 非正或超过 1 天
- **THEN** 构建连接池时抛出 `ArgumentException`
- **AND** 不创建任何客户端

#### Scenario: 选项克隆隔离

- **WHEN** 构建连接池后修改原 `ChannelClientPoolOptions` 或其中的 `ClientOptions`
- **THEN** 运行中的连接池继续使用构建时的配置
- **AND** 后续创建的客户端不受修改影响
