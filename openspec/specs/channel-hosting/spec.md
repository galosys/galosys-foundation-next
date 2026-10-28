# channel-hosting Specification

## Purpose
定义 `ChannelServer` 与通用宿主的集成方式：由调用方 runner 或 `ChannelServerGroupHostedService` 映射启停、从 `IServiceScopeFactory` 创建每连接作用域、通过 `ChannelServerDefinition` 注册多服务器，并保持示例行为等价。

## Requirements

### Requirement: 宿主生命周期

`ChannelServer` MUST be hostable by a caller-provided `IHostedService` runner or by `ChannelServerGroupHostedService` when `AddChannelServerGroup()` is used. Start MUST return only after binding; a bind failure MUST make `IHost.StartAsync` throw without leaving servers listening. Stop MUST wait for in-flight connections per each server's `ShutdownTimeout`, and the group hosted service MUST pass `CancellationToken.None`. Container disposal MUST dispose the group and its servers idempotently.

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

### Requirement: 每连接作用域来自容器

The implementation SHALL satisfy this requirement. 当 `ChannelServer` 构造时传入 `IServiceScopeFactory` 时，每个连接必须创建独立作用域。`ChannelContext.Services` 必须能解析注册为 Scoped 的服务；不同连接必须获得不同的 Scoped 实例；连接关闭后作用域必须被释放。

#### Scenario: 两个连接获得不同 Scoped 实例

- **WHEN** 注册 `AddScoped<ScopedProbe>()`，把容器的 `IServiceScopeFactory` 传给 `ChannelServer`，并接受两个连接
- **THEN** 两个连接得到不同的实例
- **AND** 每个实例只属于其连接

#### Scenario: 连接结束后释放作用域

- **WHEN** 连接关闭
- **THEN** 该连接的 Scoped 服务被释放
- **AND** 不会影响其他连接的作用域

#### Scenario: 手动构造的服务器不受影响

- **WHEN** 手动构造 `ChannelServer` 且未传入 `IServiceScopeFactory`
- **THEN** `ChannelContext.Services` 返回空提供程序
- **AND** 连接处理仍然正常

### Requirement: 示例迁移行为等价

The implementation SHALL satisfy this requirement. `NetServer.ConsoleApp` 必须使用 `ChannelServer` + `ChannelServerRunner` 托管服务器，保持监听 `0.0.0.0:8000`、打印收到的文本、每秒发送 `hello` 的现有行为，并在 Ctrl+C 时优雅停机。

#### Scenario: 启动后打印 hello

- **WHEN** 运行 `dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
- **THEN** 控制台每秒输出一条包含 `hello` 的日志
- **AND** 监听端口为 8000

#### Scenario: Ctrl+C 优雅退出

- **WHEN** 对运行中的示例按 Ctrl+C
- **THEN** 进程在 `ShutdownTimeout` 内退出
- **AND** 不出现 `NotImplementedException` 或未观察的异常

### Requirement: 多服务器注册

`ChannelServerDefinition` MUST be public sealed in `Microsoft.Extensions.Hosting.Net` with a non-empty `Name`, snapshotted options, and a pipeline delegate, rejecting blank names, null options, and null delegates. `AddChannelServer` MUST append a singleton without global options. `AddChannelServerGroup` MUST idempotently register one group and one hosted service built from all definitions with the container's `ILoggerFactory` and `IServiceScopeFactory`.

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
