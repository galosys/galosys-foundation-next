## ADDED Requirements

### Requirement: 宿主集成

The implementation SHALL allow a `ChannelServerGroup` to be hosted by `ChannelServerGroupHostedService`. The hosted service MUST forward `IHostedService.StartAsync` to `ChannelServerGroup.StartAsync` with the host cancellation token, forward `StopAsync` to `ChannelServerGroup.StopAsync(CancellationToken.None)`, and forward `IAsyncDisposable.DisposeAsync` to the group. The hosted service MUST NOT change the group's ordering, rollback, aggregation, or idempotence semantics. A group start failure MUST propagate to the host, and a group stop failure MUST surface as the group's `AggregateException`.

#### Scenario: 宿主启停映射到组

- **WHEN** 宿主启动和停止一个由 `ChannelServerGroupHostedService` 托管的组
- **THEN** 组内所有 server 按组语义启动和停止
- **AND** 宿主启动返回时所有 server 已绑定
- **AND** 宿主停止返回时所有 server 已停止

#### Scenario: 组启动失败传播到宿主

- **WHEN** 组中某个 server 启动失败，宿主调用 `StartAsync`
- **THEN** hosted service 向宿主传播组的原始异常
- **AND** 组已完成回滚，不会留下已启动的 server

#### Scenario: 宿主释放幂等

- **WHEN** 宿主容器释放 hosted service，随后再次释放组
- **THEN** 不会重复释放 server
- **AND** 不抛出 `ObjectDisposedException`
