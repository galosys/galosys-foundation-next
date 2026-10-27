## ADDED Requirements

### Requirement: 协议契约

The implementation SHALL provide public interfaces `IChannelProtocol` and `IChannelProtocolSelector` in the `Microsoft.Extensions.Hosting.Net` namespace. `IChannelProtocol` MUST expose a non-empty `Name` and a `HandleAsync(ChannelContext context, CancellationToken cancellationToken)` method returning `ValueTask`. `IChannelProtocolSelector` MUST expose `SelectAsync(ChannelContext context, CancellationToken cancellationToken)` returning `ValueTask<IChannelProtocol?>`; a null result MUST mean that no protocol matched. Neither interface may own, dispose, or complete the connection transport.

#### Scenario: 协议实现可被选择

- **WHEN** 一个实现 `IChannelProtocol` 的类型返回非空 `Name`，并实现 `HandleAsync`
- **THEN** 选择器可以返回该实例
- **AND** 分发器可以调用其 `HandleAsync`

#### Scenario: 选择器返回 null 表示无匹配

- **WHEN** `IChannelProtocolSelector.SelectAsync` 返回 `null`
- **THEN** 分发器把该结果视为无匹配
- **AND** 不会尝试调用任何协议

### Requirement: 分发器匹配处理器委托

The implementation SHALL provide a public sealed `ProtocolDispatcher` whose `HandleAsync(ChannelContext context)` method returns `Task` and matches the `ChannelHandlerDelegate` signature. The constructor MUST accept a non-null `IChannelProtocolSelector`. `ProtocolDispatcher.HandleAsync` MUST be usable directly as `pipeline.Run(dispatcher.HandleAsync)` without an adapter, and the dispatcher MUST NOT require a specific server type, pipeline middleware, or DI container.

#### Scenario: 作为终端处理器挂载

- **WHEN** 调用方创建 `ProtocolDispatcher`，并通过 `pipeline.Run(dispatcher.HandleAsync)` 配置 `ChannelServer`
- **THEN** 代码编译通过，方法组转换为 `ChannelHandlerDelegate`
- **AND** 每个连接进入分发器

#### Scenario: null 选择器被拒绝

- **WHEN** 使用 null 构造 `ProtocolDispatcher`
- **THEN** 构造函数抛出 `ArgumentNullException`

### Requirement: 选择与调用顺序

`ProtocolDispatcher.HandleAsync` MUST capture `context.ConnectionClosed` as the cancellation token, call the selector exactly once, and invoke the selected protocol exactly once when a protocol is returned. The selector MUST be called before the protocol, and the protocol MUST receive the same cancellation token. The dispatcher MUST NOT retry selection, call a second selector, or invoke a protocol after `ConnectionClosed` has been cancelled.

#### Scenario: 选择器先于协议执行

- **WHEN** 分发器处理一个连接，选择器返回协议 A
- **THEN** 事件顺序为 `select` 后 `handle`
- **AND** 选择器只被调用一次
- **AND** 协议 A 只被调用一次

#### Scenario: 协议收到连接关闭令牌

- **WHEN** 分发器选择协议并把控制权交给协议
- **THEN** 协议收到的 `CancellationToken` 等于 `context.ConnectionClosed`

#### Scenario: 分发前连接已关闭

- **WHEN** 进入 `HandleAsync` 时 `context.ConnectionClosed` 已取消
- **THEN** 分发器直接返回
- **AND** 选择器不被调用

#### Scenario: 选择后连接关闭

- **WHEN** 选择器返回协议后、调用协议前 `ConnectionClosed` 变为已取消
- **THEN** 分发器不再调用协议
- **AND** 不产生协议错误

### Requirement: 失败与中止语义

When the selector returns null or throws an exception, `ProtocolDispatcher` MUST abort the connection with an `InvalidDataException` by calling `ChannelContext.Abort`. A selector exception MUST be preserved as the `InnerException` of the `InvalidDataException` unless it is already an `InvalidDataException`, in which case it MAY be reused. The dispatcher MUST NOT rethrow selector failures to the caller. Exceptions thrown by the selected protocol MUST NOT be caught or converted by the dispatcher; they MUST propagate to the server's normal handler-error path. Cancellation caused by `ConnectionClosed` MUST NOT produce an `InvalidDataException`.

#### Scenario: 无匹配协议中止连接

- **WHEN** 选择器返回 null
- **THEN** `context.AbortReason` 是 `InvalidDataException`
- **AND** 连接被中止
- **AND** 分发器不抛出异常

#### Scenario: 选择器异常被包装

- **WHEN** 选择器抛出 `IOException`
- **THEN** `context.AbortReason` 是 `InvalidDataException`
- **AND** 其 `InnerException` 是原始 `IOException`
- **AND** 分发器不把 `IOException` 直接抛给调用方

#### Scenario: 选择器抛出的 InvalidDataException 不被双重包装

- **WHEN** 选择器抛出 `InvalidDataException`
- **THEN** `context.AbortReason` 是同一个 `InvalidDataException` 实例
- **AND** 不会出现 `InvalidDataException` 嵌套自身

#### Scenario: 连接关闭导致的取消不产生协议错误

- **WHEN** `ConnectionClosed` 已取消，选择器抛出 `OperationCanceledException`
- **THEN** 分发器返回，不调用 `Abort`
- **AND** `context.AbortReason` 保持 null

#### Scenario: 协议异常向上传播

- **WHEN** 选中的协议 `HandleAsync` 抛出 `InvalidOperationException`
- **THEN** 该异常从 `ProtocolDispatcher.HandleAsync` 传播
- **AND** 分发器不把它转换为 `InvalidDataException`

### Requirement: 选择器只窥探不消费

An `IChannelProtocolSelector` implementation MUST inspect the connection input without consuming bytes. It MUST use `ChannelContext.Reader.ReadAsync` and MUST call `AdvanceTo(buffer.Start, buffer.End)` after each successful read, leaving `Consumed` at `buffer.Start`. It MUST NOT complete, dispose, or replace the transport. When no protocol matches, the input MUST remain readable for diagnostics or for any subsequent handling outside the dispatcher.

#### Scenario: 未知协议不消费输入

- **WHEN** 连接输入为 `XYZ`，选择器读取并窥探后返回 null，分发器中止连接
- **THEN** 再次从同一 `PipeReader` 读取仍能得到 `XYZ`
- **AND** 选择器没有把 `Consumed` 推进到 `buffer.Start` 之后

#### Scenario: 选择器不释放传输

- **WHEN** 选择器完成一次选择
- **THEN** `ChannelContext.Transport` 仍可被协议或 server 收尾逻辑使用
- **AND** 选择器没有调用 `Complete`、`DisposeAsync` 或 `Abort`
