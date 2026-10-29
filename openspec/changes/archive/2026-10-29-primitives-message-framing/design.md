## Context

P1 交付了自包含 socket 传输、`ChannelContext`、管线、优雅停机和每连接 scope，但业务处理器只能拿到 `PipeReader` 的原始字节。P2 的设计文档明确把消息层拆成两个粒度：`IMessageFramer` 负责“消息到哪结束”，`IMessageCodec<T>` 负责“一帧 payload 是什么对象”。本 change 实现前半部分，不引入任何序列化依赖。

现有 `ChannelServerBase`/`ChannelServer` 和管线不需要修改；framer 是纯内存类型，可以在 handler、测试或未来的客户端中复用。

## Goals / Non-Goals

**Goals:**

- 提供稳定的字节级分帧契约 `IMessageFramer` 和带 `Consumed`/`Examined` 的 `FrameParseResult`。
- 提供 `LengthPrefixedFramer`、`LineFramer`、`PassThroughFramer` 三个首批实现。
- 用 `LengthFieldFramerOptions`、`LineFramerOptions` 表达最大帧、分隔符和 CRLF 裁剪行为。
- 用纯内存测试锁定半包、粘包、跨段、零长度、超限和格式错误。
- 更新 README，使调用方理解 frame 与 codec 的职责边界。

**Non-Goals:**

- 不实现 `IMessageCodec<T>`、`ChannelSession<T>` 或任何 session 生命周期。
- 不实现 protobuf、MessagePack、压缩、加密或具体协议。
- 不支持可变长度字段偏移、`LengthAdjustment`、`InitialBytesToStrip` 等 DotNetty 全量参数；首版只实现固定 4 字节大端长度头。
- 不把 framer 做成管线中间件，也不改动 `ChannelPipeline`。
- 不引入 `Microsoft.AspNetCore.*` 或新的 NuGet 包。

## Decisions

### 1. 分帧与消息编解码彻底分层

`IMessageFramer` 只回答“边界在哪”，输入是连续字节流，输出是一帧 payload 和消费位置；它不知道 JSON、UTF-8 或业务类型。`IMessageCodec<T>` 只处理一帧内部字节。这样 3 种分帧 × 3 种序列化只需要 6 个实现，而不是 9 个合并实现；写侧也能把“先编码 payload、再加长度头”放在一个明确的位置。

### 2. `FrameParseResult` 必须携带 `Consumed`/`Examined`

不使用 `bool TryParse(ref ReadOnlySequence<byte>, out frame)`。`PipeReader.AdvanceTo` 需要同时表达“看到哪里”和“消费到哪里”：

- 帧不完整：`Consumed = buffer.Start`，`Examined = buffer.End`，表示“看过了，但一个字节都没消费”。
- 帧完整：`Consumed = frameEnd`，`Examined = frameEnd`，只消费当前帧。
- 有状态 framer 可以调用 `NeedMoreData(consumed, examined)` 先消费帧头；首批三个 framer 都是无状态的，不采用这条路径。
- 格式错误：直接抛 `InvalidDataException`，由 session 中止连接。

`Complete` 提供两个重载：一个用 `frame.End` 作为消费位置，另一个显式接收 `consumed`，供 `LineFramer` 在 payload 之外消费分隔符。

### 3. 长度前缀首版固定 4 字节大端

`LengthPrefixedFramer` 的帧格式是 `4-byte big-endian payload length + payload`。首版不暴露 `LengthFieldOffset`/`LengthAdjustment`/`InitialBytesToStrip`，避免实现一半的兼容矩阵；后续需要时再按 DotNetty 的 `LengthFieldBasedFrameDecoder` 扩展。

- 长度字段本身不足 4 字节：`NeedMoreData(buffer)`，不消费。
- 声明长度大于 `MaxFrameSize`：在等待 payload 之前抛 `InvalidDataException`，防止慢速攻击和 OOM。
- 声明长度为 0：允许通过，返回空帧；是否合法由 codec 决定。
- 长度加法用 `checked((long)HeaderSize + payloadLength)`，避免 `uint` 回绕或负长度。
- 写侧先写 4 字节头，再写 payload；不采用“先占位、回头回填”的方案，因为 `IBufferWriter` 的段可能已经切换。

### 4. 换行 framer 是字节级的

`LineFramer` 只识别一个分隔符字节，默认 `\n`；UTF-8 解码属于 codec，不放进 framer。

- 找不到分隔符：buffer 超过 `MaxFrameSize` 时抛 `InvalidDataException`，否则 `NeedMoreData(buffer)`。
- 找到分隔符：payload 是分隔符之前的字节；`Consumed` 指向分隔符之后，确保分隔符只被消费一次。
- `TrimCarriageReturn = true` 时，若 payload 最后一个字节是 `\r`，从 payload 中裁掉；这覆盖 `\r\n` 被拆到两个 buffer 段的情况。
- 写侧只追加分隔符，不额外追加 `\r`；需要 CRLF 的对端应在协议层明确，或后续增加写侧选项。
- 空行是合法的零长度帧；是否接受由 codec 决定。

### 5. 透传 framer 明确“当前 buffer 即一帧”

`PassThroughFramer` 不做边界判断：当前 `ReadAsync` 拿到的非空 buffer 整体作为一帧，空 buffer 返回 `NeedMoreData`。它用于兼容“调用方自己保证一次读取就是一条消息”的旧用法，不保证多次写入不会被合并；该限制写入 XML 文档和 README。

### 6. 命名空间与目录

- 分帧契约和实现放在 `src/Galosys.Foundation.Primitives/Framing/`，命名空间为 `Microsoft.Extensions.Hosting.Net`，与核心宿主类型一致。
- `*Options` 放在 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/`，命名空间为 `Microsoft.Extensions.Options`，遵循仓库的“命名空间寄生”约定。
- 类型使用中文 XML 文档；公共成员均带 `<summary>`。

### 7. 选项快照与校验

framer 构造函数接收可空的 options，复制需要的值到 readonly 字段，不保存调用方的可变对象。`MaxFrameSize <= 0` 时抛 `ArgumentOutOfRangeException`；`LineFramerOptions.Separator` 允许任意字节，但 `Separator == '\r'` 且 `TrimCarriageReturn == true` 时应在 XML 文档中说明裁剪行为可能不直观。写侧 payload 超过 `MaxFrameSize` 也抛 `InvalidDataException`，与读侧保持同一错误类型。

## Risks / Trade-offs

- **无状态 `LineFramer` 可能重复扫描**：每次 `Parse` 都从 buffer 起点查找分隔符，最坏情况在超长行上产生平方级扫描。`MaxFrameSize` 会限制单帧上限；后续可增加扫描游标。
- **`PassThroughFramer` 的粘包语义**：一次读取可能包含多次写入。它只为兼容旧用法，不作为推荐协议；文档必须明确。
- **CRLF 裁剪的歧义**：`TrimCarriageReturn` 只影响读侧，写侧只发 `\n`；如果对端严格要求 CRLF，需要协议层自行处理。
- **只支持 4 字节大端长度头**：无法直接适配小端或自定义长度字段；首版接受这个限制，扩展点保留在 options/实现内部。
- **空帧策略分散**：framer 放行零长度帧，codec 决定是否合法。调用方需要理解两层职责，文档要给出示例。
- **格式错误直接抛异常**：framer 不做容错跳过；错误流通常已不可信，由 session 中止连接更安全。

## Migration Plan

1. 新增 `Framing/IMessageFramer.cs` 与 `FrameParseResult.cs`，先锁定契约和位置语义。
2. 新增两个 Options 类型，再实现 `LengthPrefixedFramer`、`LineFramer`、`PassThroughFramer`。
3. 新增 `FramingTests.cs`，按“单字节喂入 → 粘包 → 跨段 → 边界值 → 错误”的顺序补齐测试。
4. 更新 Primitives README 的消息分帧章节。
5. 运行 Primitives 构建、focused framing 测试和全量测试。本 change 不修改任何现有消费方，回滚只需删除新增文件并还原 README。
6. `primitives-message-codecs` 可在本 change 合并前并行开发；session 必须等 framing 与 codecs 都合入。

## Open Questions

无。可变长度字段、多字节分隔符和有状态扫描游标明确留给后续 change。
