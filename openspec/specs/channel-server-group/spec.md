# channel-server-group Specification

## Purpose
定义 `ChannelServerGroup` 的多服务器组合语义：顺序启动、失败逆序回滚、并行停机并聚合异常、幂等释放，以及 `ChannelServerGroupHostedService` 到宿主生命周期的映射。

## Requirements

### Requirement: 组构造与服务器集合

`ChannelServerGroup` MUST be a public sealed type in `Microsoft.Extensions.Hosting.Net` implementing `IAsyncDisposable` and `IDisposable`. The constructor MUST accept `params IChannelServer[] servers`, reject a null array or null element, defensively copy the array, and expose `IReadOnlyList<IChannelServer> Servers`. An empty group MUST be valid, and the group MUST NOT alter a server's options, `LocalEndPoint`, connection handling, or `ShutdownTimeout`.

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

### Requirement: 顺序启动与失败回滚

`StartAsync` MUST start servers sequentially in `Servers` order with the caller's token. On any failure, including cancellation, it MUST stop the servers started by this call in reverse order with `CancellationToken.None`, then rethrow the original exception instance. Rollback failures MAY be attached to `Exception.Data`, but MUST NOT replace the original. Servers whose start did not complete MUST NOT be stopped.

#### Scenario: 第二个服务器绑定失败时回滚第一个

- **WHEN** 第一个 server 绑定到空闲 loopback 端点，第二个 server 绑定到已被占用的端口，调用 `StartAsync`
- **THEN** `StartAsync` 抛出原始的 `SocketException`
- **AND** 第一个 server 已被停止，`LocalEndPoint` 回到 null
- **AND** 第二个 server 保持未启动状态
- **AND** 不会留下第一个 server 的监听 socket

#### Scenario: 启动顺序按 Servers 顺序

- **WHEN** 组中包含两个 server，第一个的启动处理器记录事件后第二个才启动
- **THEN** 第一个 server 的启动完成事件先于第二个 server 的启动开始事件
- **AND** 两个 server 最终都处于监听状态

#### Scenario: 启动取消触发回滚

- **WHEN** 第一个 server 已启动，传入的取消令牌在启动第二个 server 前被取消
- **THEN** `StartAsync` 抛出 `OperationCanceledException`
- **AND** 第一个 server 被停止，`LocalEndPoint` 回到 null

### Requirement: 并行停机与失败聚合

`StopAsync` MUST call `StopAsync` on every server in parallel with the caller's token and wait for all calls. Every observed failure, including cancellation, MUST be reported in one `AggregateException`; a failure MUST NOT skip remaining servers. The group MUST NOT impose a group-level timeout, and each server keeps its own `ShutdownTimeout`.

#### Scenario: 统一停机停止所有服务器

- **WHEN** 两个 server 正在监听，调用 `StopAsync`
- **THEN** 两个 server 的 `LocalEndPoint` 都变为 null
- **AND** 两个端点都不再接受新连接
- **AND** `StopAsync` 在全部 server 停止后返回

#### Scenario: 单个 server 停机失败也使用 AggregateException

- **WHEN** 组中一个 server 的 `StopAsync` 抛出异常，另一个正常停止
- **THEN** 正常 server 仍然完成停止
- **AND** 组抛出 `AggregateException`，其 inner exceptions 包含该失败

#### Scenario: 多个 server 停机失败全部暴露

- **WHEN** 两个 server 的 `StopAsync` 分别抛出不同异常
- **THEN** 组等待两个停止尝试都完成
- **AND** `AggregateException.InnerExceptions` 同时包含两个异常

#### Scenario: 停机取消只影响等待

- **WHEN** 传入已取消的令牌调用 `StopAsync`
- **THEN** 所有 server 的停止流程仍然被启动
- **AND** 组以 `AggregateException` 暴露取消异常或等待失败
- **AND** 不会因为取消而跳过某个 server

### Requirement: 幂等释放

`DisposeAsync` MUST stop and dispose every server and MUST be idempotent for repeated or concurrent calls after a successful disposal. `Dispose()` MUST synchronously wait for the same operation. A failure on one server MUST NOT skip the remaining servers, and all failures MUST be exposed in an `AggregateException`.

#### Scenario: 重复释放不抛异常

- **WHEN** 组启动两个 server 后连续调用 `DisposeAsync` 两次
- **THEN** 两次调用都正常完成
- **AND** 两个 server 的 `LocalEndPoint` 都为 null
- **AND** 第二次调用不会再次释放 server

#### Scenario: Dispose 与 DisposeAsync 幂等

- **WHEN** 先调用 `DisposeAsync`，再调用 `Dispose`
- **THEN** 同步调用正常返回，不抛出 `ObjectDisposedException`
- **AND** 所有 server 保持已释放状态

#### Scenario: 释放期间失败仍尝试全部服务器

- **WHEN** 组中一个 server 的释放抛出异常
- **THEN** 组仍然释放剩余 server
- **AND** 最终抛出包含该失败的 `AggregateException`

### Requirement: 宿主集成

`ChannelServerGroupHostedService` MUST host a `ChannelServerGroup` by forwarding `IHostedService.StartAsync` with the host token, `StopAsync` as `ChannelServerGroup.StopAsync(CancellationToken.None)`, and `IAsyncDisposable.DisposeAsync` to the group. It MUST NOT change the group's ordering, rollback, aggregation, or idempotence semantics; start failures propagate unchanged, and stop failures surface as the group's `AggregateException`.

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
