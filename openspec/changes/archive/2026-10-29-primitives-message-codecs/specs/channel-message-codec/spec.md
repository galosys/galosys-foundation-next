## ADDED Requirements

### Requirement: 帧级编解码契约

The implementation SHALL provide `IMessageCodec<T>` with synchronous `T Decode(ReadOnlySequence<byte> frame)` and `void Encode(IBufferWriter<byte> writer, T message)` members. A codec MUST operate on exactly one already-framed payload, MUST NOT add or remove length headers or delimiters, and MUST NOT read from or write to a `PipeReader`/`PipeWriter` directly. `Decode` MUST complete before the caller calls `AdvanceTo`; `Encode` MUST write only payload bytes. Codecs MUST be reusable across sessions and MUST NOT retain mutable state between calls.

#### Scenario: codec 只处理一帧 payload

- **WHEN** 调用方把 framer 返回的一帧传给 `Decode`
- **THEN** codec 只解释该帧内部字节
- **AND** 不尝试读取下一帧或等待更多数据

#### Scenario: Encode 不写边界

- **WHEN** 调用 `Encode` 写入一个消息
- **THEN** 输出只包含 payload 字节
- **AND** 不包含长度头、分隔符或其他分帧信息

#### Scenario: 同一 codec 被多个会话复用

- **WHEN** 两个连接各自创建 session，但共享同一个 codec 实例
- **THEN** 两次 `Decode`/`Encode` 互不影响
- **AND** codec 不保留上一次调用的消息状态

### Requirement: JSON 消息编解码

The implementation SHALL provide `JsonMessageCodec<T>` using `Utf8JsonReader` for decoding and `Utf8JsonWriter` for encoding. `Decode` MUST accept a multi-segment `ReadOnlySequence<byte>` without requiring the caller to concatenate segments, MUST throw `JsonException` for an empty frame, invalid JSON, a `null` result, or trailing content after the first JSON value, and MUST snapshot `JsonSerializerOptions` at construction. `Encode` MUST reject a `null` message with `ArgumentNullException` and MUST write exactly one JSON value.

#### Scenario: JSON 往返

- **WHEN** 使用 `JsonMessageCodec<Message>` 编码一个消息，再用同一配置解码
- **THEN** 解码结果与原始消息字段一致
- **AND** 输出是单个 JSON 值

#### Scenario: 多段 JSON 帧

- **WHEN** 一帧 JSON 被拆到多个 `ReadOnlySequence` 段中
- **THEN** `Decode` 仍能成功反序列化
- **AND** 不需要调用方先拼接数组

#### Scenario: 无效 JSON

- **WHEN** 帧内容不是合法 JSON
- **THEN** `Decode` 抛出 `JsonException`
- **AND** 不返回默认值

#### Scenario: 空 JSON 帧

- **WHEN** 传入空帧
- **THEN** `Decode` 抛出 `JsonException`
- **AND** 不把空内容解释为 `null` 或 `default(T)`

#### Scenario: 尾随内容

- **WHEN** 帧中包含一个 JSON 值后还有额外 token
- **THEN** `Decode` 抛出 `JsonException`
- **AND** 不静默忽略多余内容

#### Scenario: JSON null

- **WHEN** 帧内容是 JSON `null`
- **THEN** `Decode` 抛出 `JsonException`
- **AND** 调用方需要使用自定义 codec 表达可空消息

### Requirement: UTF-8 文本编解码

The implementation SHALL provide `Utf8StringCodec` that encodes and decodes `string` using strict UTF-8. `Decode` MUST reject an empty frame with `InvalidDataException`, MUST reject invalid UTF-8 with `DecoderFallbackException` instead of returning replacement characters, and MUST decode a multibyte character only after the framer has delivered the complete frame. `Encode` MUST reject `null` with `ArgumentNullException` and MUST write the UTF-8 bytes without a terminator or delimiter.

#### Scenario: 多字节字符完整解码

- **WHEN** 一帧包含完整的多字节 UTF-8 字符（例如中文或 emoji）
- **THEN** `Decode` 返回对应字符串
- **AND** 不出现替换字符或截断

#### Scenario: 多段序列中的多字节字符

- **WHEN** 一个完整 UTF-8 字符的字节被拆到不同的 `ReadOnlySequence` 段中
- **THEN** `Decode` 仍返回完整字符
- **AND** 分段位置不影响结果

#### Scenario: 非法 UTF-8

- **WHEN** 帧中包含非法 UTF-8 字节序列
- **THEN** `Decode` 抛出 `DecoderFallbackException`
- **AND** 不返回包含 `�` 的字符串

#### Scenario: 空文本帧

- **WHEN** 传入空帧
- **THEN** `Decode` 抛出 `InvalidDataException`
- **AND** 不返回空字符串

#### Scenario: 文本编码不追加分隔符

- **WHEN** 调用 `Encode` 写入 `"hello"`
- **THEN** 输出只有 `hello` 的 UTF-8 字节
- **AND** 不追加 `\n`、`\0` 或其他终止符

### Requirement: 原始字节编解码

The implementation SHALL provide `RawMessageCodec` as `IMessageCodec<ReadOnlyMemory<byte>>`. `Decode` MUST always return an independent copy of the frame bytes, including for single-segment sequences, so the result remains valid after `PipeReader.AdvanceTo`. `Encode` MUST copy the supplied memory into the writer without retaining it. A zero-length frame MUST decode to an empty `ReadOnlyMemory<byte>`.

#### Scenario: 解码结果是独立拷贝

- **WHEN** 从一个可复用的缓冲区解码原始字节，随后修改原缓冲区
- **THEN** 已返回的 `ReadOnlyMemory<byte>` 内容保持不变
- **AND** 调用方可以在 `AdvanceTo` 之后继续使用该结果

#### Scenario: Raw 往返

- **WHEN** 使用 `RawMessageCodec` 编码一段字节，再解码
- **THEN** 返回的字节序列与原始输入完全一致
- **AND** 不添加长度头或分隔符

#### Scenario: 零长度 Raw 帧

- **WHEN** 解码一个空帧
- **THEN** 返回空的 `ReadOnlyMemory<byte>`
- **AND** 不抛出异常

### Requirement: 编解码错误与配置快照

The implementation SHALL surface codec failures as the documented exception types and MUST NOT swallow them. `JsonMessageCodec<T>` MUST copy the supplied `JsonSerializerOptions` at construction so later mutation of the original options does not change an existing codec. Codecs MUST NOT call `ChannelContext.Abort`, close a transport, or mutate framing state; connection-level error handling belongs to the session.

#### Scenario: 构造后修改 JSON 选项

- **WHEN** 构造 `JsonMessageCodec<T>` 后修改原 `JsonSerializerOptions` 的命名策略或 converters
- **THEN** 已构造 codec 的序列化行为保持不变
- **AND** 后续解码/编码使用构造时的配置

#### Scenario: codec 不负责关闭连接

- **WHEN** `Decode` 或 `Encode` 抛出异常
- **THEN** codec 只向调用方传播异常
- **AND** 不直接关闭连接或修改 `ChannelContext`
