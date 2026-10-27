# channel-server-transport Specification

## Purpose
定义自包含 socket 服务端传输的生命周期、连接上下文、作用域释放、优雅停机、资源配置与依赖边界。
## Requirements
### Requirement: 监听器启动语义

The implementation SHALL satisfy this requirement. `IChannelServer.StartAsync` 必须在监听端点成功绑定并开始接受连接后才返回；返回时 `LocalEndPoint` 必须非 null。绑定失败（端口占用、地址不可用、权限不足）必须向调用方抛出异常，且服务器保持未启动状态。`StartAsync` 不得阻塞到服务器停机。

#### Scenario: 绑定成功后立即返回

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

### Requirement: 组合式服务器入口

The implementation SHALL provide a public sealed `ChannelServer` that implements `IChannelServer` without requiring inheritance. The constructor MUST accept `ChannelServerOptions`, an `Action<IChannelPipeline>` pipeline configuration callback, and optional `ILoggerFactory` / `IServiceScopeFactory` dependencies. The options MUST be cloned so independent `ChannelServer` instances do not share mutable configuration.

#### Scenario: 组合式服务器启动并回显

- **WHEN** 使用 `ChannelServer` 绑定 loopback 端点，并通过 `pipeline => pipeline.Run(handler)` 配置回显处理器
- **THEN** `StartAsync` 返回后 `LocalEndPoint` 非 null
- **AND** 客户端连接后可以收到完整回显
- **AND** `StopAsync` 后不再接受新连接

#### Scenario: 两个实例使用独立配置

- **WHEN** 创建两个 `ChannelServer`，分别绑定不同的 loopback 端点并设置不同的 `ShutdownTimeout` / `MaxConnections`
- **THEN** 两个实例同时监听且 `LocalEndPoint` 不同
- **AND** 停止其中一个不会停止另一个
- **AND** 修改原始 `ChannelServerOptions` 不影响任一已构造实例

### Requirement: 唯一公开服务器实现

The implementation SHALL expose sealed `ChannelServer` as the only public server implementation. `ChannelServerBase` MUST be removed from the assembly's public API, and `ChannelServerCore` MUST remain internal. All lifecycle requirements in `channel-server-transport` MUST apply to `ChannelServer`.

#### Scenario: 程序集不再暴露 ChannelServerBase

- **WHEN** 通过反射检查 `Galosys.Foundation.Primitives` 程序集中的 `Microsoft.Extensions.Hosting.Net.ChannelServerBase`
- **THEN** 该类型不存在
- **AND** `ChannelServer` 是 public sealed 类型并实现 `IChannelServer`
- **AND** `ChannelServerCore` 不是 public 类型

#### Scenario: 组合式服务器继续满足生命周期

- **WHEN** 使用 `ChannelServer` 绑定 loopback 端点并启动
- **THEN** 绑定成功后才返回，`LocalEndPoint` 非 null
- **AND** 停止后不再接受新连接
- **AND** 重复 `StopAsync` / `DisposeAsync` 不抛异常

### Requirement: pipeline 配置与启动失败

`configurePipeline` MUST be invoked during `StartAsync` before the listener is bound. The callback MUST call `IChannelPipeline.Run`; when no terminal handler is configured, startup MUST fail with `InvalidOperationException` and `LocalEndPoint` MUST remain null. `ChannelServer` MUST preserve the `StartAsync` / `RunAsync` / `StopAsync` / `DisposeAsync` semantics defined for `IChannelServer`.

#### Scenario: 缺少 Run 时启动失败

- **WHEN** 使用一个不调用 `Run` 的 pipeline 配置回调启动 `ChannelServer`
- **THEN** `StartAsync` 抛出 `InvalidOperationException`
- **AND** `LocalEndPoint` 保持 null
- **AND** 不会留下监听 socket

### Requirement: 连接建立与身份

The implementation SHALL satisfy this requirement. 每个被接受的连接必须对应一个 `ChannelContext` 实例，并具有非空且在该服务器内唯一的 `ConnectionId`。`LocalEndPoint` 与 `RemoteEndPoint` 必须反映底层连接端点。`ChannelContext` 的公开属性不得提供 setter，外部代码不得构造或伪造连接上下文；`Items` 是连接内可变字典，`UseTransport`/`Abort`/`DisposeAsync` 是显式连接操作。

#### Scenario: 两个连接获得不同身份

- **WHEN** 两个客户端连接到同一服务器
- **THEN** 两个 `ChannelContext` 的 `ConnectionId` 非空且互不相同
- **AND** 每个上下文的 `RemoteEndPoint` 与对应客户端端点匹配

#### Scenario: 上下文不可伪造

- **WHEN** 库使用方尝试在程序集外调用 `ChannelContext` 构造函数
- **THEN** 编译失败（构造函数为 internal）

### Requirement: 连接关闭与作用域释放顺序

The implementation SHALL satisfy this requirement. 当 `ChannelServer` 构造时传入 `IServiceScopeFactory` 时，每个连接必须拥有独立的 `IServiceScope`，通过 `ChannelContext.Services` 暴露。未传入工厂的手工构造场景使用共享空作用域（无 DI），仅用于测试或无容器宿主。连接关闭时，`ConnectionClosed` 令牌必须在作用域释放之前取消，且恰好取消一次；通过 `Register` 注册的回调不保证在作用域释放前全部执行完毕。处理器返回、连接被对端重置、`Abort` 调用三种情况都必须遵守该顺序。

#### Scenario: 处理器返回后关闭

- **WHEN** 处理器正常返回
- **THEN** `ConnectionClosed` 触发一次
- **AND** 该连接的作用域随后被释放

#### Scenario: 对端重置连接

- **WHEN** 客户端在处理器读取期间强制关闭 socket
- **THEN** 处理器观察到读取完成或异常
- **AND** `ConnectionClosed` 恰好触发一次，作用域被释放

#### Scenario: 空闲处理器观察到对端重置

- **WHEN** 处理器没有读取数据，只在等待 `ConnectionClosed`，客户端强制重置连接
- **THEN** `ConnectionClosed` 触发，处理器被唤醒
- **AND** 作用域随后被释放

#### Scenario: 客户端半关闭不触发 ConnectionClosed

- **WHEN** 客户端只关闭发送方向（FIN），服务器仍可写入
- **THEN** 处理器通过 `Reader` 观察到输入结束
- **AND** `ConnectionClosed` 不在半关闭时提前触发，处理器仍可发送响应

#### Scenario: 主动中止连接

- **WHEN** 处理器调用 `ChannelContext.Abort`
- **THEN** 连接立即终止，`ConnectionClosed` 恰好触发一次
- **AND** 处理器返回后不再向该连接写入数据

### Requirement: 优雅停机

The implementation SHALL satisfy this requirement. `StopAsync` 必须停止接受新连接，等待在途连接完成，超过 `ChannelServerOptions.ShutdownTimeout` 后强制中止剩余连接。`StopAsync` 与 `DisposeAsync` 必须幂等，重复调用不得抛异常。`DisposeAsync` 必须中止所有活动连接并释放监听器。

#### Scenario: 停机等待在途连接

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

#### Scenario: 处理器异常不影响其他连接

- **WHEN** 一个连接的处理器抛出异常，同时另一个连接保持正常
- **THEN** 抛出异常的连接被中止且 `ConnectionClosed` 触发
- **AND** 服务器继续接受和处理其他连接

#### Scenario: 框架完成传输收尾

- **WHEN** 处理器正常返回且未操作 `Transport`
- **THEN** 框架完成输入/输出并释放连接
- **AND** 客户端观察到流结束

### Requirement: 数据完整性与传输关闭语义

The implementation SHALL satisfy this requirement. 传输必须按顺序完整发送处理器写入的所有字节，处理部分发送，并在输入/输出 Pipe 超过配置阈值时施加背压。正常关闭必须排空已提交的输出后再关闭 socket；`Abort` 必须立即终止，不保证未发送数据被刷新。客户端半关闭（只关闭发送方向）后，服务器必须仍能读取剩余数据并向客户端发送响应。

#### Scenario: 优雅关闭排空输出

- **WHEN** 处理器写入大于输出缓冲区的数据后正常返回
- **THEN** 客户端在流结束前收到完整数据
- **AND** 数据内容与写入顺序一致

#### Scenario: 未读入站数据不导致响应丢失

- **WHEN** 客户端发送的数据超过服务器已消费量，处理器写入响应后正常返回
- **THEN** 服务器在关闭前短暂排空内核接收缓冲区
- **AND** 客户端收到完整响应并以流结束（FIN）收尾，而不是连接重置

#### Scenario: 客户端半关闭后回显

- **WHEN** 客户端发送数据后关闭 socket 的发送方向
- **THEN** 服务器读取到剩余数据和流结束
- **AND** 客户端仍能收到服务器回显的数据，随后观察到流结束

#### Scenario: 大消息触发背压与多次写入

- **WHEN** 客户端与服务器互相发送 1 MiB 数据
- **THEN** 所有字节被完整接收且内容一致
- **AND** 接收/发送循环不会因单次缓冲不足而丢字节

### Requirement: 服务器配置

The implementation SHALL satisfy this requirement. `ChannelServerOptions` 必须提供 `EndPoint`、`Backlog`、`MaxConnections`、`NoDelay`、`KeepAlive`、`LingerState`、`ReuseAddress`、`DualMode`、`ShutdownTimeout`、`MaxReadBufferSize`、`MaxWriteBufferSize`、`CreateBoundListenSocket`。`EndPoint` 为 null、`Backlog` 小于等于 0、超时小于等于 0、读写缓冲小于等于 0 时，启动前必须抛出 `ArgumentException` 或 `OptionsValidationException`。

#### Scenario: 缺少端点

- **WHEN** 使用未设置 `EndPoint` 的 `ChannelServerOptions` 启动服务器
- **THEN** 启动失败并抛出参数校验异常，不进行绑定

#### Scenario: 自定义 backlog 生效

- **WHEN** 将 `Backlog` 设置为 1 并启动服务器
- **THEN** 服务器使用该值创建监听 socket
- **AND** 启动成功

#### Scenario: 非法超时值

- **WHEN** `ShutdownTimeout` 设置为零或负值
- **THEN** 启动前抛出参数校验异常

#### Scenario: 读写缓冲配置为 Pipe 阈值

- **WHEN** 设置 `MaxReadBufferSize` 或 `MaxWriteBufferSize`
- **THEN** 对应输入/输出 Pipe 使用该值作为暂停写入阈值
- **AND** 未消费数据超过阈值时接收或写入方被背压

### Requirement: 核心依赖边界

The implementation SHALL satisfy this requirement. `Galosys.Foundation.Primitives` 核心程序集不得引用任何 `Microsoft.AspNetCore.*` 程序集，也不得通过 `FrameworkReference` 引入 `Microsoft.AspNetCore.App`。传输实现必须基于 .NET 基础库（`System.Net.Sockets`、`System.IO.Pipelines` 等），使非 Web 宿主可以无 ASP.NET Core 共享框架引用本库。

#### Scenario: 核心程序集无 ASP.NET 引用

- **WHEN** 检查 `Galosys.Foundation.Primitives` 的 csproj、还原资产与程序集引用
- **THEN** 不存在 `Microsoft.AspNetCore.*` 条目
- **AND** 自动化测试在出现此类引用时失败

#### Scenario: 非 Web 宿主引用

- **WHEN** 一个普通控制台宿主引用 `Galosys.Foundation.Primitives`
- **THEN** 宿主无需安装 ASP.NET Core 共享框架即可构建和运行
