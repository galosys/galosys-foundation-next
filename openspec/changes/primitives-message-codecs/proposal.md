## Why

分帧只解决“消息到哪结束”，调用方仍需要把一帧 payload 转换成对象。如果把序列化塞进 framer，换 JSON、UTF-8 或未来 protobuf 时就要重写分帧；写侧还会为长度前缀重复序列化。本 change 在 `primitives-message-framing` 之上增加独立的帧级编解码契约，首批只做 `System.Text.Json`、UTF-8 文本和原始字节。

## What Changes

- 新增 `IMessageCodec<T>`：同步 `Decode(ReadOnlySequence<byte>)` 与 `Encode(IBufferWriter<byte>, T)`，只处理恰好一帧 payload。
- 新增 `JsonMessageCodec<T>`：使用 `Utf8JsonReader`/`Utf8JsonWriter`，原生支持 `ReadOnlySequence<byte>`，拒绝空帧、`null`、无效 JSON 和多余尾随内容。
- 新增 `Utf8StringCodec`：严格 UTF-8 解码，拒绝空帧和非法字节；多字节字符只在完整帧上解码。
- 新增 `RawMessageCodec`：返回 `ReadOnlyMemory<byte>` 的独立拷贝，文档说明其生命周期不依赖 Pipe 缓冲区。
- 新增 `MessageCodecTests.cs`，覆盖 JSON 往返、无效/空 JSON、UTF-8 多字节、空帧以及 Raw 拷贝语义。
- 更新 Primitives README，说明 codec 与 framer 的分工和缓冲区生命周期。
- 本 change 不实现 session、framer、protobuf/MessagePack、压缩或加密；可并行开发，但集成顺序排在 framing 之后。

## Capabilities

### New Capabilities

- `channel-message-codec`: 帧级编解码契约，以及 JSON、UTF-8 文本和原始字节三个内置 codec 的输入输出、错误与生命周期语义。

### Modified Capabilities

无。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Codecs/IMessageCodec.cs`、`JsonMessageCodec.cs`、`Utf8StringCodec.cs`、`RawMessageCodec.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `IMessageCodec<T>`、`JsonMessageCodec<T>`、`Utf8StringCodec`、`RawMessageCodec`；不修改现有传输、管线和服务器 API。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/MessageCodecTests.cs` 新增纯内存测试；现有测试不需要改写。
- 依赖：不新增 NuGet 包；仅使用 `System.Text.Json`、`System.Buffers` 和 .NET 基础库。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~MessageCodecTests`。
- 顺序：本 change 可与 `primitives-message-framing` 并行开发；`primitives-message-session` 依赖两者合入，示例迁移最后执行。
