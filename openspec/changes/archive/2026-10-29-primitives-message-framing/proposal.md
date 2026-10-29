## Why

P1 的传输层只提供原始字节流，调用方必须自己处理半包、粘包和最大帧限制。P2 的消息层需要先把“消息边界”从“对象序列化”中拆出来；否则每增加一种序列化都要重写分帧，写侧还要为长度前缀序列化两遍。本 change 只交付字节级分帧契约和三个内置 framer，为后续 codec 与 session 提供稳定底座。

## What Changes

- 新增 `IMessageFramer` 与 `FrameParseResult`，明确 `Consumed`/`Examined` 语义：帧不完整时不消费任何字节，帧完整时只消费这一帧，格式错误抛 `InvalidDataException`。
- 新增 `LengthPrefixedFramer`：默认 4 字节大端长度头，`MaxFrameSize` 上限，长度加法使用 checked 运算，零长度帧由 framer 放行。
- 新增 `LineFramer`：默认 `\n` 分隔符，可选裁剪分隔符前的 `\r`，正确处理分隔符与 CRLF 跨 buffer 段的情况。
- 新增 `PassThroughFramer`：把当前可用 buffer 作为一帧，兼容现有原始字节用法。
- 新增 `LengthFieldFramerOptions`、`LineFramerOptions`（位于 `Microsoft.Extensions.Options`），构造 framer 时快照并校验。
- 新增 `FramingTests.cs`，覆盖单字节喂入、一次多帧、零长度、最大帧、长度字段跨段、分隔符/CRLF 跨段、非法长度和透传。
- 更新 Primitives README，说明 frame 与 codec 的分工，并给出 framer 的最小用法。
- 本 change 不实现 `IMessageCodec<T>`、`IChannelSession<T>`、TLS、客户端或具体业务协议。

## Capabilities

### New Capabilities

- `channel-message-framing`: 字节级分帧契约、位置语义、长度前缀/换行/透传 framer，以及分帧选项的上限与格式错误行为。

### Modified Capabilities

无。

## Impact

- 代码：新增 `src/Galosys.Foundation.Primitives/Framing/IMessageFramer.cs`、`FrameParseResult.cs`、`LengthPrefixedFramer.cs`、`LineFramer.cs`、`PassThroughFramer.cs`；新增 `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/LengthFieldFramerOptions.cs`、`LineFramerOptions.cs`；修改 `src/Galosys.Foundation.Primitives/README.md`。
- 公共 API：新增 `IMessageFramer`、`FrameParseResult`、`LengthPrefixedFramer`、`LineFramer`、`PassThroughFramer`、`LengthFieldFramerOptions`、`LineFramerOptions`；不修改现有传输、管线和服务器 API。
- 测试：`tests/Galosys.Foundation.Primitives.Tests/FramingTests.cs` 新增纯内存测试；现有 67 个测试不需要改写。
- 依赖：不新增 NuGet 包；仅使用 .NET 基础库的 `System.Buffers`、`System.Buffers.Binary`、`System.IO`。
- 验证：`dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`、`dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~FramingTests`。
- 顺序：本 change 是 P2 的第一个 change；`primitives-message-codecs` 可与本 change 并行开发，但集成测试按“framing → codecs → session → sample”的顺序执行。
