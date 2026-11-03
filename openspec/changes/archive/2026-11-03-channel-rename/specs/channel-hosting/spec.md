# Spec Delta

## MODIFIED Requirements

### Requirement: 多服务器注册

`ChannelServerDefinition` MUST be public sealed in `Galosys.Foundation.Channel` with a non-empty `Name`, snapshotted options, pipeline delegate, rejecting blank names, null options or delegates; `AddChannelServer` MUST append a singleton, no global options; `AddChannelServerGroup` MUST idempotently register group and hosted service from definitions with container's `ILoggerFactory`/`IServiceScopeFactory`; `ChannelServerGroupHostedService` MUST be public sealed in `Microsoft.Extensions.Hosting`.

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
