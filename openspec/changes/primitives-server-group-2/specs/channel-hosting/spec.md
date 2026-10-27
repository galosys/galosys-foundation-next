## ADDED Requirements

### Requirement: 多服务器注册

The implementation SHALL provide a public sealed `ChannelServerDefinition` in the `Microsoft.Extensions.Hosting.Net` namespace with a non-empty `Name`, a `ChannelServerOptions` instance, and an `Action<IChannelPipeline>` pipeline configuration delegate. The constructor MUST reject a null or whitespace name, null options, and a null delegate, and MUST snapshot the options so later mutations of the caller's object do not change the definition. The implementation SHALL provide `IServiceCollection` extensions `AddChannelServer(ChannelServerDefinition)` and `AddChannelServerGroup()` in the `Microsoft.Extensions.DependencyInjection` namespace.

`AddChannelServer` MUST append the definition as a singleton and MUST NOT register a global `ChannelServerOptions`. `AddChannelServerGroup` MUST register exactly one `ChannelServerGroup` built from all registered definitions using the container's `ILoggerFactory` and `IServiceScopeFactory`, and exactly one `ChannelServerGroupHostedService`. The registration order of definitions MUST be the start order of the group. Each definition MUST keep its own options, and every created `ChannelServer` MUST receive the shared scope factory. Calling `AddChannelServerGroup` more than once MUST be idempotent. Registering definitions without `AddChannelServerGroup` MUST leave them inert, with no listener started.

#### Scenario: 两个定义绑定独立端点

- **WHEN** 注册两个 `ChannelServerDefinition`，分别使用不同的 loopback 端点和不同的 `ShutdownTimeout`，然后调用 `AddChannelServerGroup()` 并启动宿主
- **THEN** 组内包含两个 server
- **AND** 两个 server 的 `LocalEndPoint` 都非 null 且端口不同
- **AND** 两个 server 分别遵守自己的 `ShutdownTimeout`

#### Scenario: 注册顺序决定启动顺序

- **WHEN** 按 A、B 的顺序调用 `AddChannelServer`，再调用 `AddChannelServerGroup()` 并启动宿主
- **THEN** `ChannelServerGroup.Servers[0]` 对应 A，`Servers[1]` 对应 B
- **AND** 启动事件顺序为 A 后 B

#### Scenario: 缺少组注册时定义不启动

- **WHEN** 只调用 `AddChannelServer(definition)` 而没有调用 `AddChannelServerGroup()`，宿主启动完成
- **THEN** 容器中不存在 `ChannelServerGroup`
- **AND** 定义中的端点没有监听
- **AND** 宿主停机和释放正常完成

#### Scenario: 重复组注册幂等

- **WHEN** 连续调用两次 `AddChannelServerGroup()`
- **THEN** 容器中只有一个 `ChannelServerGroup` 注册
- **AND** 只有一个 `ChannelServerGroupHostedService` 注册
- **AND** 宿主启动后每个定义只绑定一次

#### Scenario: 启动失败后不留下已启动服务器

- **WHEN** 第一个定义绑定成功，第二个定义绑定到已占用端口，宿主调用 `StartAsync`
- **THEN** 宿主启动抛出原始绑定异常
- **AND** 第一个 server 已被停止，`LocalEndPoint` 回到 null
- **AND** 第二个 server 保持未启动

#### Scenario: scope 工厂传播到每个定义

- **WHEN** 注册一个 Scoped 服务、两个定义和 `AddChannelServerGroup()`，分别连接两个 server 的处理器解析该服务
- **THEN** 两个连接获得不同的 Scoped 实例
- **AND** 每个实例只属于其连接
- **AND** 连接关闭后对应 scope 被释放

## MODIFIED Requirements

### Requirement: 宿主生命周期

The implementation SHALL satisfy this requirement. `ChannelServer` MUST be hostable either by a caller-provided `IHostedService` runner or by `ChannelServerGroupHostedService` when `AddChannelServerGroup()` is used. The runner or hosted service `StartAsync` MUST return only after the underlying server or group has completed binding; a bind failure MUST make `IHost.StartAsync` throw and MUST NOT leave previously started servers listening. `StopAsync` MUST stop accepting new connections and wait for in-flight connections according to each server's `ChannelServerOptions.ShutdownTimeout`. `ChannelServerGroupHostedService.StopAsync` MUST pass `CancellationToken.None` to `ChannelServerGroup.StopAsync` so the host timeout does not truncate per-server shutdown. Container disposal MUST dispose the group and its servers without requiring the caller to stop them first.

#### Scenario: 宿主启动后即可连接

- **WHEN** 调用 `IHost.StartAsync`，runner 或 `ChannelServerGroupHostedService` 完成绑定并返回
- **THEN** 每个已启动 server 的 `LocalEndPoint` 非 null
- **AND** 客户端可以立即连接

#### Scenario: 端口占用导致宿主启动失败

- **WHEN** 配置的端口已被占用，调用 `IHost.StartAsync`
- **THEN** `StartAsync` 抛出 `SocketException`
- **AND** 宿主不会报告启动成功
- **AND** 同组中已经启动的 server 被停止

#### Scenario: 宿主停机等待在途连接

- **WHEN** 存在未完成的连接且 `ShutdownTimeout` 未到期，调用 `IHost.StopAsync`
- **THEN** 所有 server 停止接受新连接
- **AND** 等待在途连接完成后再返回

#### Scenario: 宿主停机超时中止

- **WHEN** 在途连接超过 `ShutdownTimeout` 仍未完成，调用 `IHost.StopAsync`
- **THEN** 服务器中止剩余连接并返回
- **AND** 宿主停机流程正常完成，不抛出未处理异常

#### Scenario: 容器释放组和服务器

- **WHEN** 宿主已启动组，随后释放宿主容器
- **THEN** 组停止并释放所有 server
- **AND** 不抛出 `ObjectDisposedException`
- **AND** 重复释放保持幂等
