## ADDED Requirements

### Requirement: 传输选择与命名管道端点

The implementation SHALL expose a public `ChannelTransportKind` with `Tcp` as the default value and `NamedPipe` as an additional value, plus a public `NamedPipeEndPoint` that carries a non-empty pipe name. `ChannelServerOptions` MUST expose a `Transport` property whose default is `ChannelTransportKind.Tcp`. `StartAsync` MUST validate that `NamedPipe` is paired with `NamedPipeEndPoint` and that `Tcp` is not paired with `NamedPipeEndPoint`; a mismatch MUST fail with `ArgumentException` before any listener is created. An undefined `ChannelTransportKind` value MUST fail with `ArgumentException` during validation. `NamedPipeEndPoint` MUST reject an empty or whitespace-only pipe name, and a name containing directory separators, with `ArgumentException`.

#### Scenario: 默认传输为 TCP

- **WHEN** 创建 `ChannelServerOptions` 并只设置 `EndPoint = new IPEndPoint(IPAddress.Loopback, 0)`
- **THEN** `Transport` 的值为 `ChannelTransportKind.Tcp`
- **AND** `ChannelServer` 启动后绑定 TCP 端点，客户端可以用 `TcpClient` 连接

#### Scenario: 显式选择命名管道

- **WHEN** 设置 `Transport = ChannelTransportKind.NamedPipe` 且 `EndPoint = new NamedPipeEndPoint(pipeName)`
- **THEN** `StartAsync` 成功返回，`LocalEndPoint` 是非 null 的 `NamedPipeEndPoint`
- **AND** `NamedPipeClientStream` 可以连接到该管道名

#### Scenario: 端点与传输不匹配

- **WHEN** 使用 `Transport = NamedPipe` 搭配 `IPEndPoint`，或使用 `Transport = Tcp` 搭配 `NamedPipeEndPoint` 调用 `StartAsync`
- **THEN** `StartAsync` 抛出 `ArgumentException`
- **AND** `LocalEndPoint` 保持 null，且没有创建 listener

#### Scenario: 非法传输枚举

- **WHEN** 将 `Transport` 设置为未定义的 `(ChannelTransportKind)99` 并启动
- **THEN** 启动抛出 `ArgumentException`（允许 `ArgumentOutOfRangeException`）
- **AND** 不进行任何绑定

#### Scenario: 非法管道名

- **WHEN** 使用空字符串、纯空白或包含 `\`、`/` 的管道名构造 `NamedPipeEndPoint`
- **THEN** 构造函数抛出 `ArgumentException`
- **AND** 不会延迟到启动阶段才失败

### Requirement: 监听器工厂与服务端内核解耦

`ChannelServerCore` MUST obtain transport listeners through an internal `IChannelListenerFactory`, selected from `ChannelServerOptions.Transport`; it MUST NOT call `SocketChannelListener.BindAsync` directly. The TCP factory MUST preserve the existing `SocketChannelListener` binding behavior. The NamedPipe factory MUST create a `NamedPipeChannelListener`. `IChannelListener`, `IChannelConnection`, `IChannelListenerFactory`, `SocketChannelListener`, `SocketChannelConnection`, `NamedPipeChannelListener` and `NamedPipeChannelConnection` MUST remain internal types and MUST NOT appear in public method signatures. The public configuration surface MUST be limited to `ChannelTransportKind`, `NamedPipeEndPoint` and `ChannelServerOptions.Transport`.

#### Scenario: 默认工厂选择 TCP

- **WHEN** 使用默认 `Transport = Tcp` 启动 `ChannelServer`
- **THEN** `ChannelServerCore` 通过 `SocketChannelListenerFactory` 创建监听器
- **AND** 现有 TCP 回显、端口占用和 socket 选项测试继续通过

#### Scenario: NamedPipe 工厂选择命名管道

- **WHEN** 使用 `Transport = NamedPipe` 启动 `ChannelServer`
- **THEN** `ChannelServerCore` 通过 `NamedPipeChannelListenerFactory` 创建监听器
- **AND** TCP 监听器不会被创建

#### Scenario: 内部传输类型不进入公共 API

- **WHEN** 反射扫描 `Galosys.Foundation.Primitives` 程序集的 public 类型和 public 成员
- **THEN** 不存在 public 的 `IChannelListener`、`IChannelConnection`、`IChannelListenerFactory`、`NamedPipeChannelListener` 或 `NamedPipeChannelConnection`
- **AND** `NamedPipeEndPoint` 与 `ChannelTransportKind` 是 public，且只作为配置/端点类型出现

### Requirement: 命名管道连接生命周期

Each accepted NamedPipe connection MUST use a `NamedPipeServerStream` and expose its byte transport through `PipeReader.Create` and `PipeWriter.Create`, wrapped by the existing `DuplexPipe` and `IChannelConnection` contract. The connection MUST be represented by a `ChannelContext` with a unique `ConnectionId`. The server MUST preserve the existing per-connection `IServiceScope`, `MaxConnections`, graceful shutdown and cancellation semantics. For a NamedPipe connection, `LocalEndPoint` MUST be a `NamedPipeEndPoint`; `RemoteEndPoint` MAY be null because Windows named pipes do not expose a peer endpoint.

#### Scenario: 命名管道回显

- **WHEN** 启动 NamedPipe 服务器并用 `NamedPipeClientStream` 发送一段字节
- **THEN** 服务器通过 `ChannelContext.Reader` / `Writer` 回显完整字节
- **AND** `ChannelContext.LocalEndPoint` 是包含同一管道名的 `NamedPipeEndPoint`

#### Scenario: 两个客户端并发连接

- **WHEN** 两个 `NamedPipeClientStream` 在服务器启动后并发连接并分别发送数据
- **THEN** 两个连接都收到各自的完整回显
- **AND** 两个 `ChannelContext.ConnectionId` 不同，`RemoteEndPoint` 均为 null

#### Scenario: 最大连接数限制仍然生效

- **WHEN** 设置 `MaxConnections = 1`，第一个 NamedPipe 客户端保持连接，第二个客户端尝试连接
- **THEN** 第二个连接被服务器拒绝并释放
- **AND** 第一个连接不受影响，服务器继续接受后续连接

#### Scenario: 优雅停机等待在途连接

- **WHEN** 一个 NamedPipe 连接的处理尚未完成，调用方调用 `StopAsync`
- **THEN** 服务器停止接受新的命名管道连接
- **AND** 在 `ShutdownTimeout` 内等待该连接完成；超时后中止剩余连接
- **AND** `StopAsync` 返回后 `LocalEndPoint` 为 null，pending 管道实例被释放

#### Scenario: 取消 RunAsync 触发正常停机

- **WHEN** 调用方启动 `RunAsync(ct)` 后取消 `ct`
- **THEN** `RunAsync` 正常返回，不向宿主抛出取消异常
- **AND** 服务器停止监听并释放 NamedPipe 资源

### Requirement: 命名管道启动失败与资源释放

NamedPipe startup MUST fail when the requested pipe name is already owned by a server that has no available instance, or when the operating system rejects the pipe creation. A failed `StartAsync` MUST leave `LocalEndPoint` null, MUST NOT leave a background accept loop, and MUST release every partially created `NamedPipeServerStream`. `Abort` MUST terminate the connection without flushing pending output; `DisposeAsync` MUST flush committed output, complete the pipe reader/writer, release the stream, and be idempotent.

#### Scenario: 管道名已被占用

- **WHEN** 另一个进程或测试已用相同管道名创建了达到实例上限的服务器，调用方再启动 NamedPipe 服务器
- **THEN** `StartAsync` 抛出 `IOException` 或 `UnauthorizedAccessException`
- **AND** `LocalEndPoint` 保持 null，且没有遗留 pending 管道实例

#### Scenario: 启动前取消

- **WHEN** 使用已取消的 `CancellationToken` 调用 `StartAsync`
- **THEN** 抛出 `OperationCanceledException`
- **AND** 不创建管道实例，`LocalEndPoint` 保持 null

#### Scenario: 中止连接立即释放

- **WHEN** 处理器调用 `ChannelContext.Abort`
- **THEN** 底层 `NamedPipeServerStream` 被释放，`ConnectionClosed` 触发一次
- **AND** 未发送的输出不保证被刷新

#### Scenario: 重复释放幂等

- **WHEN** 对同一个 NamedPipe 服务器或连接连续调用两次 `DisposeAsync`
- **THEN** 第二次调用正常返回，不抛异常
- **AND** 管道句柄不会重复释放导致未处理异常

### Requirement: Windows 平台与测试隔离

The NamedPipe transport SHALL be validated on Windows. Every automated NamedPipe test MUST use a unique pipe name derived from a fresh GUID so that parallel tests and concurrent developer sessions do not share a pipe. The README MUST document the Windows-only validation scope, the unique-name requirement, and the null `RemoteEndPoint` behavior.

#### Scenario: 测试管道名唯一

- **WHEN** 两个 NamedPipe 测试并行启动
- **THEN** 它们使用不同的管道名
- **AND** 一个测试的启动/停机不会占用另一个测试的管道

#### Scenario: 文档说明平台限制

- **WHEN** 阅读 Primitives README 的 NamedPipe 章节
- **THEN** 可以看到 Windows 限定、唯一管道名要求和 `NamedPipeClientStream` 客户端示例
- **AND** 文档明确说明 `RemoteEndPoint` 为 null 以及 TCP 专属选项在 NamedPipe 下被忽略
