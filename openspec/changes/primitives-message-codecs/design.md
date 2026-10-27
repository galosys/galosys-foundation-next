## Context

`primitives-message-framing` 提供 `IMessageFramer`，把字节流切成一帧帧 payload。消息层还缺另一半：把一帧 payload 反序列化成 `T`，以及把 `T` 序列化成一帧 payload。设计文档明确要求这两个粒度分开，且 codec 必须同步完成；异步压缩、加密或请求关联不属于这一层。

本 change 只依赖 `System.Text.Json` 和 .NET 基础库，不依赖任何传输或 session 类型。它可以和 framing 并行实现，但集成测试和后续 session 按 framing → codecs 的顺序执行。

## Goals / Non-Goals

**Goals:**

- 提供同步、单帧、无状态的 `IMessageCodec<T>`。
- 提供 `JsonMessageCodec<T>`，原生支持多段 `ReadOnlySequence<byte>`。
- 提供严格的 `Utf8StringCodec` 和拷贝语义明确的 `RawMessageCodec`。
- 用纯内存测试锁定往返、错误、空帧、多字节和生命周期。
- 更新 README，让调用方理解 codec 不处理长度头/分隔符。

**Non-Goals:**

- 不实现 protobuf、MessagePack、`IObjectSerializer` 适配器或自定义二进制 codec。
- 不实现异步 codec、压缩、加密、请求/响应关联或批处理。
- 不实现 framing、session、TLS 或客户端。
- 不修改 `ChannelPipeline`，也不把 codec 做成管线中间件。

## Decisions

### 1. codec 是同步的、只处理一帧

`IMessageCodec<T>` 的输入是 framer 已经切好的一帧 payload，输出是 `T`；它不知道长度头、分隔符、半包或粘包。序列化是内存操作，接口保持同步：

```csharp
public interface IMessageCodec<T>
{
    T Decode(ReadOnlySequence<byte> frame);
    void Encode(IBufferWriter<byte> writer, T message);
}
```

需要异步压缩/加密时，在 session 外再包一层装饰器，不把 codec 变成 async，也不让 codec 持有 Pipe。

### 2. JSON 使用 `Utf8JsonReader`/`Utf8JsonWriter`

`JsonMessageCodec<T>` 使用 `Utf8JsonReader(ReadOnlySequence<byte>, JsonReaderOptions)`，因此不需要先把多段 buffer 拼成数组；写侧使用 `Utf8JsonWriter` 直接写入 `IBufferWriter<byte>`。

- 空帧直接抛 `JsonException`，不把“没有内容”解释为 `default(T)`。
- `null` 结果抛 `JsonException`；消息 codec 不接受 JSON `null` 作为有效消息。
- 反序列化后继续 `reader.Read()`；若还有 token，说明帧内有多余内容，抛 `JsonException`。
- `JsonSerializerOptions` 在构造时复制，避免调用方在运行中修改影响已创建的 codec。
- `JsonReaderOptions` 从 `JsonSerializerOptions` 的 `AllowTrailingCommas`、`ReadCommentHandling`、`MaxDepth` 派生，保持读写配置一致。
- 无效 JSON 由 `Utf8JsonReader`/`JsonSerializer` 抛 `JsonException`，codec 不吞异常。

### 3. UTF-8 解码是严格的，且只在完整帧上发生

`Utf8StringCodec` 使用 `new UTF8Encoding(false, throwOnInvalidBytes: true)`：

- 空帧抛 `InvalidDataException`，避免把缺失内容当成空字符串。
- 非法 UTF-8 抛 `DecoderFallbackException`，不返回替换字符。
- 单段序列直接 `GetString(frame.FirstSpan)`；多段序列先 `ToArray()` 再解码，保证跨段的多字节字符完整。framer 已保证一帧完整，因此不会出现“半个字符”被解码的情况。
- 写侧先 `GetByteCount`，再 `GetSpan`/`GetBytes`/`Advance`，不产生中间字符串数组。

### 4. Raw codec 返回独立拷贝

`RawMessageCodec` 的 `T` 是 `ReadOnlyMemory<byte>`。`Decode` 总是返回新的 `byte[]`/`ReadOnlyMemory<byte>` 拷贝，单段和多段路径都一样；调用方可以在 `PipeReader.AdvanceTo` 之后继续持有返回值。`Encode` 直接写入 `message.Span`，不复制。XML 文档和 README 必须明确：`ReadOnlySequence<byte>` 本身只在 `AdvanceTo` 之前有效，Raw codec 的返回值不受此限制。

### 5. 命名空间、目录与错误类型

- codec 契约和实现放在 `src/Galosys.Foundation.Primitives/Codecs/`，命名空间为 `Microsoft.Extensions.Hosting.Net`。
- 不新增 options 类型；JSON 选项沿用 `JsonSerializerOptions`。
- 错误类型按失败原因区分：JSON 用 `JsonException`，UTF-8 空帧用 `InvalidDataException`，非法 UTF-8 用 `DecoderFallbackException`，`null` 消息用 `ArgumentNullException`。
- codec 本身不中止连接；session 捕获 codec 异常后负责 `Abort`。

### 6. codec 无状态、可复用

`JsonMessageCodec<T>` 只持有只读的序列化配置；`Utf8StringCodec` 的 `UTF8Encoding` 是线程安全的；`RawMessageCodec` 无字段。一个 codec 实例可以被多个 session 并发使用，前提是调用方不修改传入的 `JsonSerializerOptions`（构造时已复制，实际也不会受影响）。

## Risks / Trade-offs

- **JSON 反序列化分配**：`Utf8JsonReader` 避免多段拼接，但 `JsonSerializer` 仍会分配对象；这是消息层可接受的成本。需要零分配协议时再引入源生成或自定义 codec。
- **空帧策略较严格**：UTF-8 和 JSON 都拒绝空 payload，空行/零长度帧会在 codec 层失败。framer 仍允许空帧，由调用方选择 codec；README 必须说明。
- **多段 UTF-8 解码会复制**：`frame.ToArray()` 只在多段时发生；单段路径零拷贝。若后续成为瓶颈，可用 `Decoder` + 池化 buffer 优化。
- **Raw codec 每次解码都复制**：这是有意为之，换取明确的生命周期；高吞吐场景可改用专用 codec。
- **JSON `null` 被拒绝**：对 `Nullable<T>` 或需要 `null` 消息的协议不适用；这类协议应使用自定义 codec。
- **codec 异常直接抛给 session**：session 会中止连接；如果业务需要跳过坏帧，应在更高层实现，不在 codec 中容错。

## Migration Plan

1. 新增 `Codecs/IMessageCodec.cs`，锁定同步单帧契约。
2. 实现 `JsonMessageCodec<T>` 的 decode/encode，并补 JSON 测试。
3. 实现 `Utf8StringCodec` 与 `RawMessageCodec`，补多字节、空帧和拷贝测试。
4. 更新 Primitives README 的消息编解码章节。
5. 运行 Primitives 构建、focused codec 测试和全量测试。本 change 不修改现有消费方，回滚只需删除新增文件并还原 README。
6. `primitives-message-session` 在 framing 与 codecs 合入后实现；示例迁移最后执行。

## Open Questions

无。protobuf/MessagePack、异步 codec、压缩和 `IObjectSerializer` 适配明确留给后续 change。
