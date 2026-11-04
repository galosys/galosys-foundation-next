# Spec Delta

## REMOVED Requirements

### Requirement: 宿主集成

**Reason**: 库不再提供宿主生命周期适配器。`ChannelServerGroup` 是纯运行时组合类型，其启停语义由调用方直接驱动，不需要框架代为映射到 `IHostedService`。

**Migration**: 改由调用方在自己的 `IHostedService` 中持有组实例，在 `StartAsync` 中调用 `ChannelServerGroup.StartAsync(ct)`，在 `StopAsync` 中调用 `ChannelServerGroup.StopAsync(CancellationToken.None)`，并在容器释放时调用 `DisposeAsync()`。原先通过 `AddChannelServerGroup()` 得到的容器注册不再需要。
