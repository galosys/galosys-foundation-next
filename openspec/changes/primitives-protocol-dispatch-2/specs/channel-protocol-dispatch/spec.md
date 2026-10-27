## ADDED Requirements

### Requirement: 前缀选择器

The implementation SHALL provide a public sealed `PrefixProtocolRule` binding a non-empty byte prefix to an `IChannelProtocol`, a public sealed `PrefixProtocolSelectorOptions` with a `MaxPrefixSize` property defaulting to 64, and a public sealed `PrefixProtocolSelector` implementing `IChannelProtocolSelector`. `PrefixProtocolSelector` MUST evaluate rules in registration order using first-match semantics: the first rule whose prefix is a prefix of the buffered bytes wins, even when a later rule has a longer prefix. Duplicate prefixes MUST be rejected at construction with `ArgumentException`. A rule whose prefix length exceeds `MaxPrefixSize`, an empty prefix, a null protocol, and a `MaxPrefixSize` less than or equal to zero MUST be rejected at construction.

The selector MUST inspect bytes only through `ChannelContext.Reader.ReadAsync`. After every successful read it MUST call `AdvanceTo(buffer.Start, buffer.End)` and MUST NOT consume bytes. When the buffered bytes are a strict prefix of a rule, the selector MUST wait for more data until a rule matches, the connection reaches EOF, or the buffered length reaches `MaxPrefixSize`. When the connection reaches EOF, no rule matches, or `MaxPrefixSize` is reached without a complete match, the selector MUST return `null`. Cancellation MUST propagate `OperationCanceledException` without calling `AdvanceTo`. The selector MUST NOT abort, complete, or replace the connection; returning `null` leaves the decision to `ProtocolDispatcher`.

#### Scenario: 注册顺序优先

- **WHEN** 规则按 `A`、`AB` 的顺序注册，输入以 `AB` 开头
- **THEN** 选择器返回 `A` 对应的协议
- **AND** 不会返回 `AB` 对应的协议

#### Scenario: 更长前缀放在前面时优先

- **WHEN** 规则按 `AB`、`A` 的顺序注册，输入以 `AB` 开头
- **THEN** 选择器返回 `AB` 对应的协议
- **AND** 证明顺序而非长度决定优先级

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

#### Scenario: 选择后不消费前缀

- **WHEN** 规则前缀为 `AB`，输入为 `ABpayload`
- **THEN** 选择器返回协议后，再次读取同一 `PipeReader` 仍得到完整的 `ABpayload`
- **AND** `Consumed` 仍在原始 `buffer.Start`

#### Scenario: 取消不消费输入

- **WHEN** 读取期间 `CancellationToken` 被取消
- **THEN** `SelectAsync` 抛出 `OperationCanceledException`
- **AND** 不调用 `AdvanceTo`
- **AND** 已到达的字节仍可被后续读取

#### Scenario: 非法规则被拒绝

- **WHEN** 传入空前缀、重复前缀、null 协议或超过 `MaxPrefixSize` 的前缀
- **THEN** `PrefixProtocolSelector` 构造函数抛出 `ArgumentException` / `ArgumentNullException`
- **AND** 不会创建可用的选择器

#### Scenario: 同端口两个协议分发

- **WHEN** 一个 `ChannelServer` 使用 `ProtocolDispatcher` 和两个前缀规则 `0x01`、`0x02`，客户端分别发送对应前缀
- **THEN** 两个连接分别进入不同协议的 handler
- **AND** 两个 handler 都能读取自己的前缀并产生不同响应
- **AND** 未知前缀连接被分发器以 `InvalidDataException` 中止，服务器继续接受后续连接
