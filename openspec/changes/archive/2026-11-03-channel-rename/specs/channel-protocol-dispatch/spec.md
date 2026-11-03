# Spec Delta

## MODIFIED Requirements

### Requirement: 协议契约

The implementation SHALL provide public interfaces `IChannelProtocol` and `IChannelProtocolSelector` in the `Galosys.Foundation.Channel` namespace. `IChannelProtocol` MUST expose a non-empty `Name` and a `HandleAsync(ChannelContext, CancellationToken)` method returning `ValueTask`. `IChannelProtocolSelector` MUST expose `SelectAsync(ChannelContext, CancellationToken)` returning `ValueTask<IChannelProtocol?>`.

#### Scenario: 协议实现可被选择

- **WHEN** 一个实现 `IChannelProtocol` 的类型返回非空 `Name`，并实现 `HandleAsync`
- **THEN** 选择器可以返回该实例
- **AND** 分发器可以调用其 `HandleAsync`
