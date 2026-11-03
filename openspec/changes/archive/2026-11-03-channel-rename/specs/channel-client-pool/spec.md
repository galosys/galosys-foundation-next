# Spec Delta

## MODIFIED Requirements

### Requirement: 客户端工厂契约

The implementation SHALL provide a public `IChannelClientFactory` in `Galosys.Foundation.Channel` with `IChannelClient Create(ChannelClientOptions options)` and a public sealed `ChannelClientFactory`. `Create` MUST reject null options with `ArgumentNullException` and MUST return a new client instance on every call; option validation remains the client constructor's responsibility.

#### Scenario: 默认工厂每次创建新客户端

- **WHEN** 连续两次调用 `ChannelClientFactory.Create(options)`
- **THEN** 返回两个不同的 `IChannelClient` 实例
- **AND** 每个实例都使用传入的选项校验并克隆配置

#### Scenario: 工厂拒绝空选项

- **WHEN** 调用 `Create(null)`
- **THEN** 抛出 `ArgumentNullException`
- **AND** 不创建客户端
