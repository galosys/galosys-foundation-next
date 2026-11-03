# Spec Delta

## MODIFIED Requirements

### Requirement: 组构造与服务器集合

`ChannelServerGroup` MUST be a public sealed type in `Galosys.Foundation.Channel` implementing `IAsyncDisposable` and `IDisposable`. The constructor MUST accept `params IChannelServer[] servers`, reject a null array or null element, defensively copy the array, and expose `IReadOnlyList<IChannelServer> Servers`. An empty group MUST be valid, and the group MUST NOT alter a server's options, `LocalEndPoint`, connection handling, or `ShutdownTimeout`.

#### Scenario: 两个服务器同时监听

- **WHEN** 用两个绑定 loopback 端点的 `ChannelServer` 构造 `ChannelServerGroup` 并调用 `StartAsync`
- **THEN** 两个 server 的 `LocalEndPoint` 都非 null
- **AND** 两个端口的实际端口不同
- **AND** 两个 server 都可以接受连接

#### Scenario: 空组是合法输入

- **WHEN** 使用无参数的 `ChannelServerGroup` 依次调用 `StartAsync`、`StopAsync` 和 `DisposeAsync`
- **THEN** 所有调用正常完成，不抛出异常
- **AND** `Servers` 为空

#### Scenario: 独立配置不被组改写

- **WHEN** 两个 server 使用不同的 `ShutdownTimeout` 和 `MaxConnections` 构造组
- **THEN** 组启动后两个 server 的行为分别遵守各自 `ChannelServerOptions`
- **AND** 停止其中一个 server 不会修改另一个 server 的配置或 `LocalEndPoint`

### Requirement: 宿主集成

`ChannelServerGroupHostedService` MUST be a public sealed type in `Microsoft.Extensions.Hosting` and MUST host a `ChannelServerGroup` by forwarding `IHostedService.StartAsync` with the host token, `StopAsync` as `ChannelServerGroup.StopAsync(CancellationToken.None)`, and `IAsyncDisposable.DisposeAsync` to the group. It MUST NOT change the group's ordering, rollback, aggregation, or idempotence semantics; start failures propagate unchanged, and stop failures surface as an `AggregateException`.

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
