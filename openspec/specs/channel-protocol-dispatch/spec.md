# channel-protocol-dispatch Specification

## Purpose
为 `ChannelServer` 连接提供协议分发能力：选择器按连接输入选择协议，分发器负责调用协议并保留连接所有权；内置前缀选择器支持同端口多协议路由。

## Requirements

### Requirement: 协议契约

The implementation SHALL provide public interfaces `IChannelProtocol` and `IChannelProtocolSelector` in the `Microsoft.Extensions.Hosting.Net` namespace. `IChannelProtocol` MUST expose a non-empty `Name` and a `HandleAsync(ChannelContext, CancellationToken)` method returning `ValueTask`. `IChannelProtocolSelector` MUST expose `SelectAsync(ChannelContext, CancellationToken)` returning `ValueTask<IChannelProtocol?>`.

#### Scenario: 协议实现可被选择

- **WHEN** 一个实现 `IChannelProtocol` 的类型返回非空 `Name`，并实现 `HandleAsync`
- **THEN** 选择器可以返回该实例
- **AND** 分发器可以调用其 `HandleAsync`

### Requirement: 选择结果与连接所有权

A null result from `IChannelProtocolSelector.SelectAsync` MUST mean that no protocol matched. `IChannelProtocol` and `IChannelProtocolSelector` MUST NOT own, dispose, or complete the connection transport.

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

When the selector returns null or throws an exception, `ProtocolDispatcher` MUST abort the connection with an `InvalidDataException` by calling `ChannelContext.Abort`, and MUST NOT rethrow selector failures to the caller. A selector exception MUST be preserved as the `InnerException` of the `InvalidDataException` unless it is already an `InvalidDataException`, in which case it MAY be reused.

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

### Requirement: 取消不是协议错误

Cancellation caused by `ConnectionClosed` MUST NOT produce an `InvalidDataException`.

#### Scenario: 连接关闭导致的取消不产生协议错误

- **WHEN** `ConnectionClosed` 已取消，选择器抛出 `OperationCanceledException`
- **THEN** 分发器返回，不调用 `Abort`
- **AND** `context.AbortReason` 保持 null

### Requirement: 协议异常传播

Exceptions thrown by the selected protocol MUST NOT be caught or converted by the dispatcher; they MUST propagate to the server's normal handler-error path.

#### Scenario: 协议异常向上传播

- **WHEN** 选中的协议 `HandleAsync` 抛出 `InvalidOperationException`
- **THEN** 该异常从 `ProtocolDispatcher.HandleAsync` 传播
- **AND** 分发器不把它转换为 `InvalidDataException`

### Requirement: 选择器只窥探不消费

An `IChannelProtocolSelector` MUST inspect input without consuming bytes via `ChannelContext.Reader.ReadAsync`. When returning a protocol or `null` it MUST call `AdvanceTo(buffer.Start, buffer.Start)` so the next reader immediately sees the same bytes; while waiting for a partial prefix it MUST call `AdvanceTo(buffer.Start, buffer.End)`. It MUST NOT complete, dispose, or replace the transport, and MUST leave unmatched input readable.

#### Scenario: 未知协议不消费输入

- **WHEN** 连接输入为 `XYZ`，选择器读取并窥探后返回 null，分发器中止连接
- **THEN** 再次从同一 `PipeReader` 读取仍能得到 `XYZ`
- **AND** 选择器没有把 `Consumed` 推进到 `buffer.Start` 之后

#### Scenario: 选择器不释放传输

- **WHEN** 选择器完成一次选择
- **THEN** `ChannelContext.Transport` 仍可被协议或 server 收尾逻辑使用
- **AND** 选择器没有调用 `Complete`、`DisposeAsync` 或 `Abort`

#### Scenario: 选择后不消费前缀

- **WHEN** 规则前缀为 `AB`，输入为 `ABpayload`
- **THEN** 选择器返回协议后，再次读取同一 `PipeReader` 仍得到完整的 `ABpayload`
- **AND** `Consumed` 仍在原始 `buffer.Start`

### Requirement: 前缀选择器类型契约

The implementation SHALL provide a public sealed `PrefixProtocolRule` binding a non-empty byte prefix to an `IChannelProtocol`, a public sealed `PrefixProtocolSelectorOptions` with a `MaxPrefixSize` property defaulting to 64, and a public sealed `PrefixProtocolSelector` implementing `IChannelProtocolSelector`.

#### Scenario: 规则绑定前缀与协议

- **WHEN** 用非空前缀和协议创建 `PrefixProtocolRule`，再用该规则创建 `PrefixProtocolSelector`
- **THEN** 选择器可以返回规则绑定的协议
- **AND** `PrefixProtocolSelectorOptions.MaxPrefixSize` 默认值为 64

### Requirement: first-match 顺序

`PrefixProtocolSelector` MUST evaluate rules in registration order using first-match semantics: the first rule whose prefix is a prefix of the buffered bytes wins, even when a later rule has a longer prefix.

#### Scenario: 注册顺序优先

- **WHEN** 规则按 `A`、`AB` 的顺序注册，输入以 `AB` 开头
- **THEN** 选择器返回 `A` 对应的协议
- **AND** 不会返回 `AB` 对应的协议

#### Scenario: 更长前缀放在前面时优先

- **WHEN** 规则按 `AB`、`A` 的顺序注册，输入以 `AB` 开头
- **THEN** 选择器返回 `AB` 对应的协议
- **AND** 证明顺序而非长度决定优先级

### Requirement: 规则校验

Duplicate prefixes, empty prefixes, null protocols, prefixes longer than `MaxPrefixSize`, and a `MaxPrefixSize` less than or equal to zero MUST be rejected at construction with `ArgumentException` or `ArgumentNullException`.

#### Scenario: 非法规则被拒绝

- **WHEN** 传入空前缀、重复前缀、null 协议或超过 `MaxPrefixSize` 的前缀
- **THEN** `PrefixProtocolSelector` 构造函数抛出 `ArgumentException` / `ArgumentNullException`
- **AND** 不会创建可用的选择器

### Requirement: 等待与终止条件

When the buffered bytes are a strict prefix of a rule, the selector MUST wait for more data until a rule matches, the connection reaches EOF, or the buffered length reaches `MaxPrefixSize`. When the connection reaches EOF, no rule matches, or `MaxPrefixSize` is reached without a complete match, the selector MUST return `null`.

#### Scenario: 半包前缀等待更多数据

- **WHEN** 规则前缀为 `ABCD`，当前只收到 `AB`，连接仍然打开
- **THEN** `SelectAsync` 不返回，继续等待
- **AND** 收到 `CD` 后返回该规则协议

#### Scenario: 未知前缀返回 null

- **WHEN** 规则前缀为 `AB`，输入以 `ZZ` 开头
- **THEN** `SelectAsync` 返回 null
- **AND** 不消费 `ZZ`

#### Scenario: EOF 时半包不匹配返回 null

- **WHEN** 规则前缀为 `ABCD`，当前只收到 `AB` 且连接已结束
- **THEN** `SelectAsync` 返回 null
- **AND** 不抛出 `InvalidDataException`；中止由分发器负责

#### Scenario: 超过 MaxPrefixSize 返回 null

- **WHEN** `MaxPrefixSize` 为 4，输入长度达到 4 且没有任何规则完整匹配
- **THEN** `SelectAsync` 返回 null
- **AND** 不会继续等待更多字节

### Requirement: 取消传播

Cancellation MUST propagate `OperationCanceledException` without calling `AdvanceTo`.

#### Scenario: 取消不消费输入

- **WHEN** 读取期间 `CancellationToken` 被取消
- **THEN** `SelectAsync` 抛出 `OperationCanceledException`
- **AND** 不调用 `AdvanceTo`
- **AND** 已到达的字节仍可被后续读取

### Requirement: 同端口前缀分发

A `ChannelServer` using `ProtocolDispatcher` with two prefix rules MUST route each connection to the matching protocol, and MUST keep accepting connections after an unknown prefix is aborted.

#### Scenario: 同端口两个协议分发

- **WHEN** 一个 `ChannelServer` 使用 `ProtocolDispatcher` 和两个前缀规则 `0x01`、`0x02`，客户端分别发送对应前缀
- **THEN** 两个连接分别进入不同协议的 handler
- **AND** 两个 handler 都能读取自己的前缀并产生不同响应
- **AND** 未知前缀连接被分发器以 `InvalidDataException` 中止，服务器继续接受后续连接
