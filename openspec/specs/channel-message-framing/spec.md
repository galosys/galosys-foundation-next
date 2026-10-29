# channel-message-framing Specification

## Purpose
定义字节级分帧契约 `IMessageFramer`/`FrameParseResult` 的位置语义，以及长度前缀、换行和透传三种 framer 的边界、上限与格式错误行为。

## Requirements

### Requirement: 字节级分帧契约

The implementation SHALL provide `IMessageFramer` as a byte-level codec contract with `FrameParseResult Parse(in ReadOnlySequence<byte> buffer)` and `void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload)`. `FrameParseResult` MUST expose `IsComplete`, `Frame`, `Consumed`, and `Examined`; when a frame is incomplete, `Consumed` MUST equal `buffer.Start` and `Examined` MUST equal `buffer.End`; when a frame is complete, `Consumed` MUST advance past exactly one frame including its delimiter or length header. Format errors MUST throw `InvalidDataException` rather than returning a partial result.

#### Scenario: 半包不消费任何字节

- **WHEN** 缓冲区中只有一个完整帧的一部分，调用 `Parse`
- **THEN** 返回 `IsComplete = false`
- **AND** `Consumed` 等于 `buffer.Start`，`Examined` 等于 `buffer.End`
- **AND** 调用方按该位置 `AdvanceTo` 后可以继续等待后续数据

#### Scenario: 完整帧只消费一帧

- **WHEN** 缓冲区中包含一个或多个完整帧，调用 `Parse`
- **THEN** 返回第一个完整帧
- **AND** `Consumed` 和 `Examined` 都指向该帧之后的边界
- **AND** 后续帧仍保留在缓冲区中

#### Scenario: 格式错误抛 InvalidDataException

- **WHEN** framer 识别到非法长度、超过上限的帧或其他无法继续解析的格式
- **THEN** `Parse` 抛出 `InvalidDataException`
- **AND** 不返回 `FrameParseResult`

### Requirement: 长度前缀分帧

The implementation SHALL provide `LengthPrefixedFramer` using a 4-byte big-endian payload length header followed by exactly that many payload bytes. The framer MUST expose `HeaderSize = 4`, MUST reject a declared length greater than `MaxFrameSize` before waiting for the payload, MUST use checked arithmetic for header-plus-length calculations, and MUST allow a zero-length payload as a complete empty frame. `WriteFrame` MUST write the big-endian length header followed by the payload and MUST reject payloads larger than `MaxFrameSize`.

#### Scenario: 单字节喂入

- **WHEN** 把一个合法长度前缀帧按每字节一次的方式喂给 `Parse`
- **THEN** 前 4 个字节以及后续不足 payload 的调用都返回 `IsComplete = false`
- **AND** 最后一个字节到达时返回完整 payload
- **AND** `Consumed` 指向整帧末尾

#### Scenario: 一个缓冲区包含多个帧

- **WHEN** 两个长度前缀帧首尾相连地放在同一个缓冲区
- **THEN** 第一次 `Parse` 只返回第一帧
- **AND** 第二次从第一次的 `Consumed` 继续解析可以返回第二帧
- **AND** 两帧 payload 内容互不混淆

#### Scenario: 长度字段跨段

- **WHEN** 4 字节长度字段被拆到两个 `ReadOnlySequence` 段中
- **THEN** `Parse` 仍能正确读出大端长度
- **AND** 在 payload 未到齐前返回 `IsComplete = false`

#### Scenario: 零长度帧

- **WHEN** 长度字段为 0
- **THEN** `Parse` 返回 `IsComplete = true`
- **AND** `Frame` 为空
- **AND** `Consumed` 前进 4 字节

#### Scenario: 超过最大帧

- **WHEN** 长度字段声明的 payload 长度大于 `MaxFrameSize`
- **THEN** `Parse` 在等待 payload 之前抛出 `InvalidDataException`
- **AND** 不会因为声明超大长度而继续缓冲

#### Scenario: 写侧写入大端长度头

- **WHEN** 调用 `WriteFrame` 写入一段 payload
- **THEN** 输出前 4 字节是大端 payload 长度
- **AND** 后续字节与 payload 完全一致

### Requirement: 换行分帧

The implementation SHALL provide `LineFramer` that finds a single separator byte (default `\n`) at the byte level and returns the bytes before it as one frame. When `TrimCarriageReturn` is enabled, a single `\r` immediately before the separator MUST be removed from the payload. The framer MUST consume the separator exactly once, MUST handle a separator or `\r\n` split across buffer segments, and MUST throw `InvalidDataException` when the buffer exceeds `MaxFrameSize` without a separator.

#### Scenario: 分隔符跨段

- **WHEN** 分隔符之前的 payload 与分隔符本身分处不同的 buffer 段
- **THEN** `Parse` 返回完整 payload
- **AND** `Consumed` 指向分隔符之后

#### Scenario: CRLF 被拆到两个段

- **WHEN** `\r` 位于第一个段末尾，`\n` 位于下一个段开头
- **THEN** `Parse` 返回不包含 `\r` 的 payload
- **AND** `Consumed` 同时越过 `\r` 和 `\n`

#### Scenario: 空行

- **WHEN** 缓冲区以分隔符开头
- **THEN** `Parse` 返回 `IsComplete = true`
- **AND** `Frame` 为空
- **AND** 分隔符被消费一次

#### Scenario: 超长行没有分隔符

- **WHEN** 缓冲区长度超过 `MaxFrameSize` 且仍未找到分隔符
- **THEN** `Parse` 抛出 `InvalidDataException`
- **AND** 不继续无限缓冲

### Requirement: 透传分帧

The implementation SHALL provide `PassThroughFramer` that treats the entire non-empty current buffer as one frame. An empty buffer MUST return an incomplete result so the caller waits for more data. `PassThroughFramer` MUST NOT add, remove, or reinterpret any bytes; its write path MUST copy the payload unchanged.

#### Scenario: 当前 buffer 作为一帧

- **WHEN** 当前缓冲区非空并调用 `Parse`
- **THEN** 返回 `IsComplete = true`
- **AND** `Frame` 与当前缓冲区内容完全一致
- **AND** `Consumed` 等于 `buffer.End`

#### Scenario: 空 buffer 等待更多数据

- **WHEN** 当前缓冲区为空并调用 `Parse`
- **THEN** 返回 `IsComplete = false`
- **AND** `Consumed` 等于 `buffer.Start`
- **AND** `Examined` 等于 `buffer.End`

### Requirement: 分帧选项与上限

The implementation SHALL provide `LengthFieldFramerOptions` and `LineFramerOptions` in the `Microsoft.Extensions.Options` namespace. Both MUST expose a positive `MaxFrameSize` with a default of 1 MiB; `LineFramerOptions` MUST expose a separator byte defaulting to `\n` and a `TrimCarriageReturn` flag defaulting to `true`. Framer constructors MUST snapshot option values so later mutation of the options object does not affect an existing framer, and MUST throw `ArgumentOutOfRangeException` when `MaxFrameSize` is not positive.

#### Scenario: 非法最大帧配置

- **WHEN** 使用 `MaxFrameSize = 0` 或负值构造 framer
- **THEN** 构造函数抛出 `ArgumentOutOfRangeException`
- **AND** 不会创建可用的 framer 实例

#### Scenario: 构造后修改 options 不影响 framer

- **WHEN** 构造 framer 后修改原 options 的 `MaxFrameSize` 或分隔符
- **THEN** 已构造的 framer 仍按构造时的值工作
- **AND** 解析和写入结果不发生变化

#### Scenario: 自定义分隔符

- **WHEN** 使用 `Separator = (byte)';'` 构造 `LineFramer` 并解析 `a;b;`
- **THEN** 第一次返回 `a`，第二次返回 `b`
- **AND** 分号只被消费一次
