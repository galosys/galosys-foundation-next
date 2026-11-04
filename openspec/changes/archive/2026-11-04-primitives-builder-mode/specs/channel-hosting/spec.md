# Spec Delta

## MODIFIED Requirements

### Requirement: 宿主生命周期

The implementation SHALL satisfy this requirement. `ChannelServer` MUST be hostable by a caller-provided `IHostedService` runner. The library MUST NOT ship a hosted service adapter and MUST NOT ship any `IServiceCollection` extension. Start MUST return only after binding; a bind failure MUST make the caller's host start throw without leaving servers listening. Stop MUST wait for in-flight connections per each server's `ShutdownTimeout`. The caller-provided runner MUST pass `CancellationToken.None` to the server's stop call so the host's shutdown deadline cannot truncate the server's own drain budget. Container disposal MUST dispose the server idempotently when the caller registers it for disposal.

#### Scenario: 宿主启动后即可连接

- **WHEN** 调用方把由构建器产出的服务器注册为 `IHostedService` 并调用 `IHost.StartAsync`
- **THEN** hosted service 完成绑定并返回
- **AND** 服务器的 `LocalEndPoint` 非 null
- **AND** 客户端可以立即连接

#### Scenario: 端口占用导致宿主启动失败

- **WHEN** 配置的端口已被占用，宿主调用 `IHost.StartAsync`
- **THEN** `StartAsync` 抛出 `SocketException`
- **AND** 宿主不会报告启动成功
- **AND** 服务器不会留下监听 socket

#### Scenario: 宿主停机等待在途连接

- **WHEN** 存在未完成的连接且 `ShutdownTimeout` 未到期，宿主调用 `IHost.StopAsync`
- **THEN** 服务器停止接受新连接
- **AND** 等待在途连接完成后再返回

#### Scenario: 宿主停机超时中止

- **WHEN** 在途连接超过 `ShutdownTimeout` 仍未完成，宿主调用 `IHost.StopAsync`
- **THEN** 服务器中止剩余连接并返回
- **AND** 宿主停机流程正常完成，不抛出未处理异常

#### Scenario: 容器释放组和服务器

- **WHEN** 宿主已启动服务器，随后释放宿主容器
- **THEN** 服务器停止并释放所有资源
- **AND** 不抛出 `ObjectDisposedException`
- **AND** 重复释放保持幂等

### Requirement: 每连接作用域来自容器

The implementation SHALL satisfy this requirement. 当构建器通过 `WithServiceScopeFactory` 传入 `IServiceScopeFactory` 时，每个连接必须创建独立作用域。`ChannelContext.Services` 必须能解析注册为 Scoped 的服务；不同连接必须获得不同的 Scoped 实例；连接关闭后作用域必须被释放。未传入作用域工厂的构建结果必须使用共享空作用域提供程序。

#### Scenario: 两个连接获得不同 Scoped 实例

- **WHEN** 调用方把容器的 `IServiceScopeFactory` 通过构建器传给服务器，并接受两个连接
- **THEN** 两个连接得到不同的实例
- **AND** 每个实例只属于其连接

#### Scenario: 连接结束后释放作用域

- **WHEN** 连接关闭
- **THEN** 该连接的 Scoped 服务被释放
- **AND** 不会影响其他连接的作用域

#### Scenario: 手动构造的服务器不受影响

- **WHEN** 构建服务器时未传入 `IServiceScopeFactory`
- **THEN** `ChannelContext.Services` 返回空提供程序
- **AND** 连接处理仍然正常

### Requirement: 示例迁移行为等价

The implementation SHALL satisfy this requirement. `NetServer.ConsoleApp` 必须使用构建器产出 `ChannelServer` 并通过 `ChannelServerRunner` 托管服务器，保持监听 `0.0.0.0:8000`、打印收到的文本、周期性地发送请求并在 Ctrl+C 时优雅停机。

#### Scenario: 启动后周期性打印收到的文本

- **WHEN** 运行 `dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
- **THEN** 控制台每秒输出一条包含收到的文本的日志
- **AND** 监听端口为 8000

#### Scenario: 服务端托管在发送端之前启动

- **WHEN** 检查示例的 hosted service 注册顺序
- **THEN** `ChannelServerRunner` 先于发送端的 hosted service 注册
- **AND** 客户端连接时监听已经就绪

#### Scenario: Ctrl+C 优雅退出

- **WHEN** 对运行中的示例按 Ctrl+C
- **THEN** 进程在 `ShutdownTimeout` 内退出
- **AND** 不出现 `NotImplementedException` 或未观察的异常

## REMOVED Requirements

### Requirement: 多服务器注册

**Reason**: 库不再提供 IoC 集成。多台服务器的编排改由 `ChannelServerGroup` 直接承担，配置由 `ChannelServerBuilder` 在调用方代码中一次性声明，不需要容器代为收集定义。

**Migration**: 改用 `ChannelServerGroup`。对每台服务器调用 `ChannelServerBuilder.Create(...).Run(...).Build()`，把结果数组传给 `new ChannelServerGroup(servers...)`，再由调用方自己的 `IHostedService` 或直接调用 `StartAsync` / `StopAsync` 管理生命周期。原先通过 `AddChannelServer(definition)` 表达的名称不再需要，服务器顺序由数组顺序决定。

## ADDED Requirements

### Requirement: 库不提供 IoC 与宿主集成

The implementation SHALL NOT ship any `IServiceCollection` extension method, hosted service adapter, or server definition type for host registration. The library MUST NOT reference `Microsoft.Extensions.Hosting.Abstractions`. The assembly MUST NOT expose types under the `Microsoft.Extensions.DependencyInjection` or `Microsoft.Extensions.Hosting` namespaces. Configuration MUST be expressed through the builder types in `Galosys.Foundation.Channel`.

#### Scenario: 程序集不暴露宿主集成类型

- **WHEN** 通过反射检查 `Galosys.Foundation.Channel` 程序集中的类型
- **THEN** 不存在 `Microsoft.Extensions.DependencyInjection.ChannelServerGroupServiceCollectionExtensions`
- **AND** 不存在 `Microsoft.Extensions.Hosting.ChannelServerGroupHostedService`
- **AND** 不存在 `Galosys.Foundation.Channel.ChannelServerDefinition`

#### Scenario: 核心程序集不引用宿主抽象包

- **WHEN** 检查 `Galosys.Foundation.Channel` 的 csproj 与还原资产
- **THEN** 不存在 `microsoft.extensions.hosting.abstractions` 条目
- **AND** 仍然引用 `microsoft.extensions.dependencyinjection.abstractions` 以支持每连接作用域
