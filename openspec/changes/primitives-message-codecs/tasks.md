# P2-2 帧级消息编解码 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增同步、单帧、无状态的 `IMessageCodec<T>`，并提供 JSON、UTF-8 文本和原始字节三个内置 codec。

**Architecture:** codec 只处理 framer 已经切好的一帧 payload；JSON 使用 `Utf8JsonReader`/`Utf8JsonWriter` 原生支持多段序列，UTF-8 使用严格编码器，Raw 返回独立拷贝。codec 不感知长度头、分隔符或连接生命周期。

**Tech Stack:** .NET 10、`System.Text.Json`、`System.Buffers`、xunit 2.9.3。

---

## 1. 契约与测试先行

- [ ] 2.1 写 JSON 往返的失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/MessageCodecTests.cs`

```csharp
using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed record TestMessage(int Id, string Text);

public sealed class MessageCodecTests
{
    [Fact]
    public void Json_RoundTrip()
    {
        var codec = new JsonMessageCodec<TestMessage>();
        var writer = new ArrayBufferWriter<byte>();

        codec.Encode(writer, new TestMessage(1, "hello"));
        var decoded = codec.Decode(new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));

        Assert.Equal(new TestMessage(1, "hello"), decoded);
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~MessageCodecTests`
Expected: 编译失败，提示 `IMessageCodec<T>` / `JsonMessageCodec<T>` 不存在。

- [ ] 2.2 新增 `IMessageCodec<T>`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Codecs/IMessageCodec.cs`

```csharp
using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>帧级编解码器：一帧 payload ↔ T；同步完成，不处理分帧边界。</summary>
    public interface IMessageCodec<T>
    {
        /// <summary>把恰好一帧 payload 解码为消息；失败时抛出 codec 约定的异常。</summary>
        T Decode(ReadOnlySequence<byte> frame);

        /// <summary>把消息编码为一帧 payload；不写长度头或分隔符。</summary>
        void Encode(IBufferWriter<byte> writer, T message);
    }
}
```

- [ ] 2.3 实现 `JsonMessageCodec<T>.Decode`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Codecs/JsonMessageCodec.cs`

**Step 1: 写入字段与构造函数**

```csharp
using System.Buffers;
using System.Text.Json;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>使用 System.Text.Json 的帧级消息编解码器。</summary>
    public sealed class JsonMessageCodec<T> : IMessageCodec<T>
    {
        private readonly JsonSerializerOptions _options;
        private readonly JsonReaderOptions _readerOptions;

        public JsonMessageCodec(JsonSerializerOptions? options = null)
        {
            _options = options is null
                ? new JsonSerializerOptions()
                : new JsonSerializerOptions(options);

            _readerOptions = new JsonReaderOptions
            {
                AllowTrailingCommas = _options.AllowTrailingCommas,
                CommentHandling = _options.ReadCommentHandling,
                MaxDepth = _options.MaxDepth,
            };
        }

        public T Decode(ReadOnlySequence<byte> frame)
        {
            if (frame.IsEmpty)
                throw new JsonException("JSON 帧不能为空。");

            var reader = new Utf8JsonReader(frame, _readerOptions);
            var value = JsonSerializer.Deserialize<T>(ref reader, _options);

            if (value is null)
                throw new JsonException("JSON 帧不能为 null。");

            if (reader.Read())
                throw new JsonException("JSON 帧包含多余内容。");

            return value;
        }
    }
}
```

**Step 2: 编译确认 decode 可被测试引用**

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 编译失败，`JsonMessageCodec<T>` 未实现 `Encode`（这是预期的中间状态）。

- [ ] 2.4 实现 `JsonMessageCodec<T>.Encode`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Codecs/JsonMessageCodec.cs`

```csharp
public void Encode(IBufferWriter<byte> writer, T message)
{
    ArgumentNullException.ThrowIfNull(writer);

    if (message is null)
        throw new ArgumentNullException(nameof(message));

    using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions
    {
        Indented = false,
        SkipValidation = false,
    });

    JsonSerializer.Serialize(jsonWriter, message, _options);
    jsonWriter.Flush();
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~MessageCodecTests`
Expected: `Json_RoundTrip` 通过。

- [ ] 2.5 补齐 JSON 错误测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/MessageCodecTests.cs`

```csharp
[Fact]
public void Json_Invalid_ThrowsJsonException()
{
    var codec = new JsonMessageCodec<TestMessage>();
    var frame = new ReadOnlySequence<byte>("{"u8.ToArray());

    Assert.Throws<JsonException>(() => codec.Decode(frame));
}

[Fact]
public void Json_Empty_ThrowsJsonException()
{
    var codec = new JsonMessageCodec<TestMessage>();

    Assert.Throws<JsonException>(() => codec.Decode(ReadOnlySequence<byte>.Empty));
}

[Fact]
public void Json_TrailingContent_ThrowsJsonException()
{
    var codec = new JsonMessageCodec<TestMessage>();
    var frame = new ReadOnlySequence<byte>("{\"Id\":1,\"Text\":\"a\"} {}"u8.ToArray());

    Assert.Throws<JsonException>(() => codec.Decode(frame));
}

[Fact]
public void Json_Null_ThrowsJsonException()
{
    var codec = new JsonMessageCodec<TestMessage>();
    var frame = new ReadOnlySequence<byte>("null"u8.ToArray());

    Assert.Throws<JsonException>(() => codec.Decode(frame));
}

[Fact]
public void Json_EncodeNull_ThrowsArgumentNullException()
{
    var codec = new JsonMessageCodec<TestMessage>();
    var writer = new ArrayBufferWriter<byte>();

    Assert.Throws<ArgumentNullException>(() => codec.Encode(writer, null!));
}
```

- [ ] 2.6 实现 `Utf8StringCodec`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Codecs/Utf8StringCodec.cs`

```csharp
using System.Buffers;
using System.IO;
using System.Text;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>严格 UTF-8 文本编解码器；空帧和非法字节都会被拒绝。</summary>
    public sealed class Utf8StringCodec : IMessageCodec<string>
    {
        private static readonly UTF8Encoding Utf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public string Decode(ReadOnlySequence<byte> frame)
        {
            if (frame.IsEmpty)
                throw new InvalidDataException("UTF-8 文本帧不能为空。");

            return frame.IsSingleSegment
                ? Utf8.GetString(frame.FirstSpan)
                : Utf8.GetString(frame.ToArray());
        }

        public void Encode(IBufferWriter<byte> writer, string message)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(message);

            var byteCount = Utf8.GetByteCount(message);
            var span = writer.GetSpan(byteCount);
            Utf8.GetBytes(message, span);
            writer.Advance(byteCount);
        }
    }
}
```

- [ ] 2.7 补齐 UTF-8 测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/MessageCodecTests.cs`

覆盖以下断言：

```csharp
[Fact]
public void Utf8_RoundTrip_Multibyte()
{
    var codec = new Utf8StringCodec();
    var writer = new ArrayBufferWriter<byte>();

    codec.Encode(writer, "你好，世界");
    var decoded = codec.Decode(new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));

    Assert.Equal("你好，世界", decoded);
}

[Fact]
public void Utf8_MultiSegment_MultibyteCharacter_Decodes()
{
    var bytes = Encoding.UTF8.GetBytes("你");
    var buffer = TestSequences.Create(bytes[..1], bytes[1..]);

    Assert.Equal("你", new Utf8StringCodec().Decode(buffer));
}

[Fact]
public void Utf8_InvalidBytes_ThrowsDecoderFallbackException()
{
    var frame = new ReadOnlySequence<byte>(new byte[] { 0xFF, 0xFE });

    Assert.Throws<DecoderFallbackException>(() => new Utf8StringCodec().Decode(frame));
}

[Fact]
public void Utf8_Empty_ThrowsInvalidDataException()
{
    Assert.Throws<InvalidDataException>(
        () => new Utf8StringCodec().Decode(ReadOnlySequence<byte>.Empty));
}

[Fact]
public void Utf8_Encode_DoesNotAppendDelimiter()
{
    var writer = new ArrayBufferWriter<byte>();
    new Utf8StringCodec().Encode(writer, "hi");

    Assert.Equal("hi"u8.ToArray(), writer.WrittenSpan.ToArray());
}
```

- [ ] 2.8 实现 `RawMessageCodec`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Codecs/RawMessageCodec.cs`

```csharp
using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>原始字节编解码器；Decode 总是返回独立拷贝。</summary>
    public sealed class RawMessageCodec : IMessageCodec<ReadOnlyMemory<byte>>
    {
        public ReadOnlyMemory<byte> Decode(ReadOnlySequence<byte> frame)
            => frame.IsSingleSegment
                ? frame.First.ToArray()
                : frame.ToArray();

        public void Encode(IBufferWriter<byte> writer, ReadOnlyMemory<byte> message)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.Write(message.Span);
        }
    }
}
```

- [ ] 2.9 补齐 Raw 测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/MessageCodecTests.cs`

```csharp
[Fact]
public void Raw_Decode_ReturnsIndependentCopy()
{
    var source = "raw"u8.ToArray();
    var decoded = new RawMessageCodec().Decode(new ReadOnlySequence<byte>(source));

    source[0] = (byte)'X';

    Assert.Equal("raw", Encoding.UTF8.GetString(decoded.Span));
}

[Fact]
public void Raw_RoundTrip()
{
    var codec = new RawMessageCodec();
    var writer = new ArrayBufferWriter<byte>();

    codec.Encode(writer, "bytes"u8.ToArray());
    var decoded = codec.Decode(new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));

    Assert.Equal("bytes"u8.ToArray(), decoded.ToArray());
}

[Fact]
public void Raw_Empty_ReturnsEmptyMemory()
{
    var decoded = new RawMessageCodec().Decode(ReadOnlySequence<byte>.Empty);

    Assert.True(decoded.IsEmpty);
}
```

- [ ] 2.10 补多段 JSON 与选项快照测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/MessageCodecTests.cs`

```csharp
[Fact]
public void Json_MultiSegment_Decodes()
{
    var bytes = Encoding.UTF8.GetBytes("{\"Id\":7,\"Text\":\"split\"}");
    var buffer = TestSequences.Create(bytes[..5], bytes[5..]);

    var decoded = new JsonMessageCodec<TestMessage>().Decode(buffer);

    Assert.Equal(new TestMessage(7, "split"), decoded);
}

[Fact]
public void Json_OptionsAreSnapshotted()
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    var codec = new JsonMessageCodec<TestMessage>(options);
    options.PropertyNamingPolicy = null;

    var writer = new ArrayBufferWriter<byte>();
    codec.Encode(writer, new TestMessage(1, "x"));

    Assert.Contains("\"id\"", Encoding.UTF8.GetString(writer.WrittenSpan));
}
```

- [ ] 2.11 运行 focused codec 测试

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~MessageCodecTests`
Expected: 全部 `MessageCodecTests` 通过。

- [ ] 2.12 更新 Primitives README 的编解码章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“## 消息分帧”之后增加：

````markdown
## 消息编解码

`IMessageCodec<T>` 只处理一帧 payload，不处理长度头、分隔符、半包或粘包。

| Codec | T | 说明 |
|---|---|---|
| `JsonMessageCodec<T>` | `T` | `Utf8JsonReader`/`Utf8JsonWriter`，支持多段序列；空帧、`null`、无效 JSON、尾随内容抛 `JsonException` |
| `Utf8StringCodec` | `string` | 严格 UTF-8；空帧抛 `InvalidDataException`，非法字节抛 `DecoderFallbackException` |
| `RawMessageCodec` | `ReadOnlyMemory<byte>` | `Decode` 返回独立拷贝，可在 `AdvanceTo` 后继续使用 |

```csharp
IMessageCodec<MyMessage> codec = new JsonMessageCodec<MyMessage>();

// frame 来自 IMessageFramer.Parse；必须在 AdvanceTo 之前完成 Decode。
var message = codec.Decode(frame);

var writer = new ArrayBufferWriter<byte>();
codec.Encode(writer, message);
```

`ReadOnlySequence<byte>` 只在 `AdvanceTo` 之前有效；需要长期持有 payload 时使用
`RawMessageCodec` 或自行拷贝。
````

- [ ] 2.13 全量构建、测试与 OpenSpec 校验

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试 + `FramingTests` + `MessageCodecTests` 全部通过。

Run: `openspec validate "primitives-message-codecs" --strict --no-interactive`
Expected: `Change 'primitives-message-codecs' is valid`。
