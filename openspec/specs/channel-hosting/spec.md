# channel-hosting Specification

## Purpose
TBD - created by archiving change primitives-socket-p1-2. Update Purpose after archive.
## Requirements
### Requirement: 服务注册

The implementation SHALL satisfy this requirement. `AddChannelServer<TChannelServer>` 必须向容器注册 `ChannelServerOptions`、`TChannelServer` 单例以及 `ChannelServerHostedService<TChannelServer>`。从容器解析 `TChannelServer` 与 `IChannelServer` 必须返回同一实例。容器释放时必须释放服务器。

#### Scenario: 同一实例

- **WHEN** 调用 `AddChannelServer<SampleChannelServer>()` 后从容器分别解析 `SampleChannelServer` 与 `IChannelServer`
- **THEN** 两个引用指向同一对象
- **AND** 该对象只被构造一次

#### Scenario: 容器释放服务器

- **WHEN** 宿主被释放且服务器仍在运行
- **THEN** 服务器停止监听并释放监听器
- **AND** 不抛出 `ObjectDisposedException`

#### Scenario: 缺少端点时注册不失败

- **WHEN** `AddChannelServer<T>` 未配置 `EndPoint`
- **THEN** 注册阶段成功
- **AND** 宿主启动阶段才失败（fail-fast 在启动而非注册）

### Requirement: 宿主生命周期

The implementation SHALL satisfy this requirement. `ChannelServerHostedService` 必须在宿主启动完成前完成绑定，在宿主停机时停止接受连接并等待在途连接。宿主启动期间绑定失败必须使 `IHost.StartAsync` 抛出异常。停机必须遵守 `ChannelServerOptions.ShutdownTimeout`。

#### Scenario: 宿主启动后即可连接

- **WHEN** 调用 `IHost.StartAsync` 成功返回
- **THEN** 服务器已完成绑定，`LocalEndPoint` 非 null
- **AND** 客户端可以立即连接

#### Scenario: 端口占用导致宿主启动失败

- **WHEN** 配置的端口已被占用，调用 `IHost.StartAsync`
- **THEN** `StartAsync` 抛出 `SocketException`
- **AND** 宿主不会报告启动成功

#### Scenario: 宿主停机等待在途连接

- **WHEN** 存在未完成的连接且 `ShutdownTimeout` 未到期，调用 `IHost.StopAsync`
- **THEN** 服务器停止接受新连接
- **AND** 等待在途连接完成后再返回

#### Scenario: 宿主停机超时中止

- **WHEN** 在途连接超过 `ShutdownTimeout` 仍未完成，调用 `IHost.StopAsync`
- **THEN** 服务器中止剩余连接并返回
- **AND** 宿主停机流程正常完成，不抛出未处理异常

### Requirement: 每连接作用域来自容器

The implementation SHALL satisfy this requirement. 服务器由 `AddChannelServer` 注册时，每个连接必须从宿主容器创建独立作用域。`ChannelContext.Services` 必须能解析注册为 Scoped 的服务；不同连接必须获得不同的 Scoped 实例；连接关闭后作用域必须被释放。

#### Scenario: 两个连接获得不同 Scoped 实例

- **WHEN** 注册 `AddScoped<ScopedProbe>()` 并接受两个连接，处理器分别解析该服务
- **THEN** 两个连接得到不同的实例
- **AND** 每个实例只属于其连接

#### Scenario: 连接结束后释放作用域

- **WHEN** 连接关闭
- **THEN** 该连接的 Scoped 服务被释放
- **AND** 不会影响其他连接的作用域

#### Scenario: 手动构造的服务器不受影响

- **WHEN** 使用 `new SampleChannelServer(options)` 手动构造且未设置 scope 工厂
- **THEN** `ChannelContext.Services` 返回空提供程序
- **AND** 连接处理仍然正常

### Requirement: 示例迁移行为等价

The implementation SHALL satisfy this requirement. `NetServer.ConsoleApp` 必须改用 `AddChannelServer<SampleChannelServer>` 托管服务器，保持监听 `0.0.0.0:8000`、打印收到的文本、每秒发送 `hello` 的现有行为，并在 Ctrl+C 时优雅停机。

#### Scenario: 启动后打印 hello

- **WHEN** 运行 `dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
- **THEN** 控制台每秒输出一条包含 `hello` 的日志
- **AND** 监听端口为 8000

#### Scenario: Ctrl+C 优雅退出

- **WHEN** 对运行中的示例按 Ctrl+C
- **THEN** 进程在 `ShutdownTimeout` 内退出
- **AND** 不出现 `NotImplementedException` 或未观察的异常

