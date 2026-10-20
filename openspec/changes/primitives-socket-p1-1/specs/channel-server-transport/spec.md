## ADDED Requirements

### Requirement: 监听器启动语义

The implementation SHALL satisfy this requirement. `IChannelServer.StartAsync` 必须在监听端点成功绑定并开始接受连接后才返回；返回时 `LocalEndPoint` 必须非 null。绑定失败（端口占用、地址不可用、权限不足）必须向调用方抛出异常，且服务器保持未启动状态。`StartAsync` 不得阻塞到服务器停机。


 绑定成功后立即返回

- **WHEN** 调用方对可用端点调用 `StartAsync`
- **THEN** `StartAsync` 在绑定完成后返回，`LocalEndPoint` 为该端点的实际绑定地址
- **AND** 调用返回后连接可以被接受

#### Scenario: 端口已占用时启动失败

- **WHEN** 同一端点已被另一个监听器占用，调用方再次调用 `StartAsync`
- **THEN** `StartAsync` 抛出 `SocketException`（`AddressAlreadyInUse`）
- **AND** `LocalEndPoint` 保持 null，后续 `StopAsync` 不抛异常

#### Scenario: 旧用法通过 RunAsync 阻塞

- **WHEN** 调用方使用 `RunAsync(ct)` 启动服务器
- **THEN** `RunAsync` 在 `StartAsync` 完成后阻塞，直到 `StopAsync` 被调用或 `ct` 取消
- **AND** `ct` 取消被视为正常停机，不向宿主抛出取消异常

### Requirement: 连接建立与身份

The implementation SHALL satisfy this requirement. 每个被接受的连接必须对应一个 `ChannelContext` 实例，并具有非空且在该服务器内唯一的 `ConnectionId`。`LocalEndPoint` 与 `RemoteEndPoint` 必须反映底层连接端点。`ChannelContext` 的公开成员必须只读，外部代码不得构造或伪造连接上下文。


 两个连接获得不同身份

- **WHEN** 两个客户端连接到同一服务器
- **THEN** 两个 `ChannelContext` 的 `ConnectionId` 非空且互不相同
- **AND** 每个上下文的 `RemoteEndPoint` 与对应客户端端点匹配

#### Scenario: 上下文不可伪造

- **WHEN** 库使用方尝试在程序集外调用 `ChannelContext` 构造函数
- **THEN** 编译失败（构造函数为 internal）

### Requirement: 连接关闭与作用域释放顺序

The implementation SHALL satisfy this requirement. 每个连接必须拥有独立的 `IServiceScope`，通过 `ChannelContext.Services` 暴露。连接关闭时，`ConnectionClosed` 必须在作用域释放之前触发且恰好触发一次。处理器返回、连接被对端重置、`Abort` 调用三种情况都必须遵守该顺序。


 处理器返回后关闭

- **WHEN** 处理器正常返回
- **THEN** `ConnectionClosed` 触发一次
- **AND** 该连接的作用域随后被释放

#### Scenario: 对端重置连接

- **WHEN** 客户端在处理器读取期间强制关闭 socket
- **THEN** 处理器观察到读取完成或异常
- **AND** `ConnectionClosed` 恰好触发一次，作用域被释放

#### Scenario: 主动中止连接

- **WHEN** 处理器调用 `ChannelContext.Abort`
- **THEN** 连接立即终止，`ConnectionClosed` 恰好触发一次
- **AND** 处理器返回后不再向该连接写入数据

### Requirement: 优雅停机

The implementation SHALL satisfy this requirement. `StopAsync` 必须停止接受新连接，等待在途连接完成，超过 `ChannelServerOptions.ShutdownTimeout` 后强制中止剩余连接。`StopAsync` 与 `DisposeAsync` 必须幂等，重复调用不得抛异常。`DisposeAsync` 必须中止所有活动连接并释放监听器。


 停机等待在途连接

- **WHEN** 一个连接的处理尚未完成且耗时小于 `ShutdownTimeout`，调用方调用 `StopAsync`
- **THEN** `StopAsync` 等待该连接完成
- **AND** 新连接在停机开始后不再被接受

#### Scenario: 停机超时后强制中止

- **WHEN** 一个连接在超过 `ShutdownTimeout` 后仍未完成，调用方调用 `StopAsync`
- **THEN** 该连接的 `ConnectionClosed` 被触发，`StopAsync` 在超时后返回
- **AND** 服务器资源被释放

#### Scenario: 重复停机

- **WHEN** `StopAsync` 或 `DisposeAsync` 被连续调用两次
- **THEN** 第二次调用正常返回，不抛异常

### Requirement: 连接级资源所有权

The implementation SHALL satisfy this requirement. 框架必须拥有连接底层传输的释放权。处理器只负责返回，不得调用 `Transport.Input.Complete`、`Transport.Output.Complete` 或释放 `ChannelContext`；框架在处理器返回后完成传输收尾。处理器抛出的异常必须被观察并导致该连接中止，不得终止接受循环或抛出到宿主。


 处理器异常不影响其他连接

- **WHEN** 一个连接的处理器抛出异常，同时另一个连接保持正常
- **THEN** 抛出异常的连接被中止且 `ConnectionClosed` 触发
- **AND** 服务器继续接受和处理其他连接

#### Scenario: 框架完成传输收尾

- **WHEN** 处理器正常返回且未操作 `Transport`
- **THEN** 框架完成输入/输出并释放连接
- **AND** 客户端观察到流结束

### Requirement: 服务器配置

The implementation SHALL satisfy this requirement. `ChannelServerOptions` 必须提供 `EndPoint`、`Backlog`、`MaxConnections`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`、`MaxReadBufferSize`、`MaxWriteBufferSize`、`IOQueueCount`、`CreateBoundListenSocket`。`EndPoint` 为 null、`Backlog` 小于等于 0、超时小于等于 0 时，启动前必须抛出 `ArgumentException` 或 `OptionsValidationException`。


 缺少端点

- **WHEN** 使用未设置 `EndPoint` 的 `ChannelServerOptions` 启动服务器
- **THEN** 启动失败并抛出参数校验异常，不进行绑定

#### Scenario: 自定义 backlog 生效

- **WHEN** 将 `Backlog` 设置为 1 并启动服务器
- **THEN** 服务器使用该值创建监听 socket
- **AND** 启动成功

#### Scenario: 非法超时值

- **WHEN** `ShutdownTimeout` 设置为零或负值
- **THEN** 启动前抛出参数校验异常
