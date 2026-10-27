# P2-1 字节级分帧 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 在 `Galosys.Foundation.Primitives` 中新增稳定、纯内存的 `IMessageFramer` 契约与长度前缀/换行/透传三个 framer。

**Architecture:** `IMessageFramer` 只负责 stream ↔ frame；`FrameParseResult` 用 `Consumed`/`Examined` 表达“看完但未消费”和“消费一帧”。三个 framer 都是无状态实现，选项在构造时快照。长度前缀固定 4 字节大端，换行分隔符是字节级，透传把当前 buffer 当一帧。

**Tech Stack:** .NET 10、`System.Buffers`、`System.Buffers.Binary`、`System.IO`、xunit 2.9.3。

---

## 1. 契约与测试先行

- [ ] 1.1 写长度前缀单字节喂入的失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/FramingTests.cs`

**Step 1: 写入测试文件骨架**

```csharp
using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class FramingTests
{
    [Fact]
    public void LengthPrefixed_OneByteFeeds_CompletesFrame()
    {
        var framer = new LengthPrefixedFramer();
        var frame = BuildLengthPrefixed("hello"u8.ToArray());
        var bytes = new List<byte>(frame.Length);

        for (var i = 0; i < frame.Length; i++)
        {
            bytes.Add(frame[i]);
            var buffer = new ReadOnlySequence<byte>(bytes.ToArray());
            var result = framer.Parse(buffer);

            if (i < frame.Length - 1)
            {
                Assert.False(result.IsComplete);
                Assert.Equal(buffer.Start, result.Consumed);
                Assert.Equal(buffer.End, result.Examined);
            }
            else
            {
                Assert.True(result.IsComplete);
                Assert.Equal("hello"u8.ToArray(), result.Frame.ToArray());
                Assert.Equal(buffer.End, result.Consumed);
            }
        }
    }

    private static byte[] BuildLengthPrefixed(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }
}
```

**Step 2: 运行测试，确认“红”**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~FramingTests`
Expected: 编译失败，提示 `LengthPrefixedFramer` / `IMessageFramer` 不存在。

- [ ] 1.2 新增 `IMessageFramer` 与 `FrameParseResult`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Framing/IMessageFramer.cs`
- Create: `src/Galosys.Foundation.Primitives/Framing/FrameParseResult.cs`

**Step 1: 定义分帧契约**

```csharp
using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>字节级编解码器：在连续字节流中定位一帧，并为一帧加上边界。</summary>
    public interface IMessageFramer
    {
        /// <summary>解析当前缓冲区；不完整时返回未消费结果，格式错误时抛出异常。</summary>
        FrameParseResult Parse(in ReadOnlySequence<byte> buffer);

        /// <summary>把一帧 payload 写入目标缓冲区，并加上长度头或分隔符。</summary>
        void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload);
    }
}
```

**Step 2: 定义结果结构**

```csharp
using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>分帧结果：完整帧、消费位置与已检查位置。</summary>
    public readonly struct FrameParseResult
    {
        private FrameParseResult(
            bool isComplete,
            ReadOnlySequence<byte> frame,
            SequencePosition consumed,
            SequencePosition examined)
        {
            IsComplete = isComplete;
            Frame = frame;
            Consumed = consumed;
            Examined = examined;
        }

        public bool IsComplete { get; }

        public ReadOnlySequence<byte> Frame { get; }

        public SequencePosition Consumed { get; }

        public SequencePosition Examined { get; }

        public static FrameParseResult NeedMoreData(in ReadOnlySequence<byte> buffer)
            => new(false, default, buffer.Start, buffer.End);

        public static FrameParseResult NeedMoreData(SequencePosition consumed, SequencePosition examined)
            => new(false, default, consumed, examined);

        public static FrameParseResult Complete(ReadOnlySequence<byte> frame)
            => new(true, frame, frame.End, frame.End);

        public static FrameParseResult Complete(ReadOnlySequence<byte> frame, SequencePosition consumed)
            => new(true, frame, consumed, consumed);
    }
}
```

- [ ] 1.3 新增两个分帧 Options

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/LengthFieldFramerOptions.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/LineFramerOptions.cs`

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>长度前缀分帧配置。</summary>
    public sealed class LengthFieldFramerOptions
    {
        /// <summary>单帧 payload 的硬上限，默认 1 MiB。</summary>
        public int MaxFrameSize { get; set; } = 1024 * 1024;
    }
}
```

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>换行分帧配置。</summary>
    public sealed class LineFramerOptions
    {
        /// <summary>单帧 payload 的硬上限，默认 1 MiB。</summary>
        public int MaxFrameSize { get; set; } = 1024 * 1024;

        /// <summary>分隔符字节，默认 <c>\n</c>。</summary>
        public byte Separator { get; set; } = (byte)'\n';

        /// <summary>是否裁剪分隔符前的一个 <c>\r</c>，默认 true。</summary>
        public bool TrimCarriageReturn { get; set; } = true;
    }
}
```

- [ ] 1.4 实现 `LengthPrefixedFramer.Parse`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Framing/LengthPrefixedFramer.cs`

**Step 1: 写入字段、构造函数与 Parse**

```csharp
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>4 字节大端长度头 + payload 的分帧器。</summary>
    public sealed class LengthPrefixedFramer : IMessageFramer
    {
        /// <summary>长度头占用 4 字节。</summary>
        public const int HeaderSize = 4;

        private readonly int _maxFrameSize;

        public LengthPrefixedFramer(LengthFieldFramerOptions? options = null)
        {
            options ??= new LengthFieldFramerOptions();
            if (options.MaxFrameSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxFrameSize 必须大于 0。");

            _maxFrameSize = options.MaxFrameSize;
        }

        public FrameParseResult Parse(in ReadOnlySequence<byte> buffer)
        {
            if (buffer.Length < HeaderSize)
                return FrameParseResult.NeedMoreData(buffer);

            Span<byte> header = stackalloc byte[HeaderSize];
            buffer.Slice(0, HeaderSize).CopyTo(header);
            var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (payloadLength > (uint)_maxFrameSize)
                throw new InvalidDataException(
                    $"帧长度 {payloadLength} 超过上限 {_maxFrameSize}。");

            var frameLength = checked((long)HeaderSize + payloadLength);
            if (buffer.Length < frameLength)
                return FrameParseResult.NeedMoreData(buffer);

            var consumed = buffer.GetPosition(frameLength);
            return FrameParseResult.Complete(buffer.Slice(HeaderSize, payloadLength), consumed);
        }
    }
}
```

**Step 2: 先让单字节测试编译**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~FramingTests`
Expected: 编译仍失败，`LengthPrefixedFramer` 未实现 `WriteFrame`（这是预期的中间状态）。

- [ ] 1.5 实现 `LengthPrefixedFramer.WriteFrame`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Framing/LengthPrefixedFramer.cs`

```csharp
public void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload)
{
    ArgumentNullException.ThrowIfNull(writer);

    if (payload.Length > _maxFrameSize)
        throw new InvalidDataException(
            $"帧长度 {payload.Length} 超过上限 {_maxFrameSize}。");

    if (payload.Length > uint.MaxValue)
        throw new InvalidDataException("帧长度超过 4 字节长度字段的表示范围。");

    var header = writer.GetSpan(HeaderSize);
    BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
    writer.Advance(HeaderSize);

    foreach (var segment in payload)
        writer.Write(segment.Span);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~FramingTests`
Expected: `LengthPrefixed_OneByteFeeds_CompletesFrame` 通过。

- [ ] 1.6 补齐长度前缀边界测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/FramingTests.cs`

**Step 1: 追加多段序列辅助类**

```csharp
internal sealed class BufferSegment : ReadOnlySequenceSegment<byte>
{
    public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

    public BufferSegment Append(ReadOnlyMemory<byte> memory)
    {
        var segment = new BufferSegment(memory)
        {
            RunningIndex = RunningIndex + Memory.Length,
        };
        Next = segment;
        return segment;
    }
}

internal static class TestSequences
{
    public static ReadOnlySequence<byte> Create(params byte[][] segments)
    {
        if (segments.Length == 0)
            return ReadOnlySequence<byte>.Empty;
        if (segments.Length == 1)
            return new ReadOnlySequence<byte>(segments[0]);

        var first = new BufferSegment(segments[0]);
        var last = first;
        for (var i = 1; i < segments.Length; i++)
            last = last.Append(segments[i]);

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
```

**Step 2: 追加测试方法**

覆盖以下断言：

```csharp
[Fact]
public void LengthPrefixed_MultipleFrames_ReturnsFirstOnly()
{
    var frame1 = BuildLengthPrefixed("one"u8.ToArray());
    var frame2 = BuildLengthPrefixed("two"u8.ToArray());
    var buffer = new ReadOnlySequence<byte>(frame1.Concat(frame2).ToArray());

    var framer = new LengthPrefixedFramer();
    var first = framer.Parse(buffer);

    Assert.True(first.IsComplete);
    Assert.Equal("one"u8.ToArray(), first.Frame.ToArray());

    var rest = buffer.Slice(first.Consumed);
    var second = framer.Parse(rest);
    Assert.True(second.IsComplete);
    Assert.Equal("two"u8.ToArray(), second.Frame.ToArray());
}

[Fact]
public void LengthPrefixed_ZeroLength_CompletesEmptyFrame()
{
    var buffer = new ReadOnlySequence<byte>(BuildLengthPrefixed(Array.Empty<byte>()));
    var result = new LengthPrefixedFramer().Parse(buffer);

    Assert.True(result.IsComplete);
    Assert.True(result.Frame.IsEmpty);
    Assert.Equal(4, buffer.GetOffset(result.Consumed));
}

[Fact]
public void LengthPrefixed_LengthFieldSplitAcrossSegments_Completes()
{
    var frame = BuildLengthPrefixed("split"u8.ToArray());
    var buffer = TestSequences.Create(frame[..2], frame[2..]);

    var result = new LengthPrefixedFramer().Parse(buffer);

    Assert.True(result.IsComplete);
    Assert.Equal("split"u8.ToArray(), result.Frame.ToArray());
}

[Fact]
public void LengthPrefixed_ExactlyMaxFrame_Completes()
{
    var payload = new byte[16];
    var framer = new LengthPrefixedFramer(new LengthFieldFramerOptions { MaxFrameSize = 16 });
    var result = framer.Parse(new ReadOnlySequence<byte>(BuildLengthPrefixed(payload)));

    Assert.True(result.IsComplete);
    Assert.Equal(16, result.Frame.Length);
}

[Fact]
public void LengthPrefixed_AboveMax_Throws()
{
    var frame = BuildLengthPrefixed(new byte[17]);
    var framer = new LengthPrefixedFramer(new LengthFieldFramerOptions { MaxFrameSize = 16 });

    Assert.Throws<InvalidDataException>(() => framer.Parse(new ReadOnlySequence<byte>(frame)));
}

[Fact]
public void LengthPrefixed_WriteFrame_WritesBigEndianHeader()
{
    var writer = new ArrayBufferWriter<byte>();
    new LengthPrefixedFramer().WriteFrame(writer, new ReadOnlySequence<byte>("abc"u8.ToArray()));

    Assert.Equal(new byte[] { 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c' }, writer.WrittenSpan.ToArray());
}
```

- [ ] 1.7 实现 `LineFramer.Parse`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Framing/LineFramer.cs`

```csharp
using System.Buffers;
using System.IO;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>按单字节分隔符切分文本帧；UTF-8 解码由 codec 负责。</summary>
    public sealed class LineFramer : IMessageFramer
    {
        private readonly int _maxFrameSize;
        private readonly byte _separator;
        private readonly bool _trimCarriageReturn;

        public LineFramer(LineFramerOptions? options = null)
        {
            options ??= new LineFramerOptions();
            if (options.MaxFrameSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxFrameSize 必须大于 0。");

            _maxFrameSize = options.MaxFrameSize;
            _separator = options.Separator;
            _trimCarriageReturn = options.TrimCarriageReturn;
        }

        public FrameParseResult Parse(in ReadOnlySequence<byte> buffer)
        {
            var separatorPosition = buffer.PositionOf(_separator);
            if (separatorPosition is null)
            {
                if (buffer.Length > _maxFrameSize)
                    throw new InvalidDataException(
                        $"未找到分隔符且缓冲区长度 {buffer.Length} 超过上限 {_maxFrameSize}。");

                return FrameParseResult.NeedMoreData(buffer);
            }

            var separatorIndex = buffer.GetOffset(separatorPosition.Value);
            if (separatorIndex > _maxFrameSize)
                throw new InvalidDataException(
                    $"帧长度 {separatorIndex} 超过上限 {_maxFrameSize}。");

            var payloadLength = separatorIndex;
            if (_trimCarriageReturn && payloadLength > 0)
            {
                var lastByte = buffer.Slice(payloadLength - 1, 1).FirstSpan[0];
                if (lastByte == (byte)'\r')
                    payloadLength--;
            }

            var consumed = buffer.GetPosition(checked(separatorIndex + 1));
            return FrameParseResult.Complete(buffer.Slice(0, payloadLength), consumed);
        }
    }
}
```

- [ ] 1.8 实现 `LineFramer.WriteFrame`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Framing/LineFramer.cs`

```csharp
public void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload)
{
    ArgumentNullException.ThrowIfNull(writer);

    if (payload.Length > _maxFrameSize)
        throw new InvalidDataException(
            $"帧长度 {payload.Length} 超过上限 {_maxFrameSize}。");

    foreach (var segment in payload)
        writer.Write(segment.Span);

    var separator = writer.GetSpan(1);
    separator[0] = _separator;
    writer.Advance(1);
}
```

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 1.9 补齐换行分帧测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/FramingTests.cs`

覆盖以下断言：

```csharp
[Fact]
public void Line_OneByteFeeds_CompletesAtSeparator()
{
    var framer = new LineFramer();
    var bytes = new List<byte>();

    foreach (var value in "hi\n"u8.ToArray())
    {
        bytes.Add(value);
        var buffer = new ReadOnlySequence<byte>(bytes.ToArray());
        var result = framer.Parse(buffer);

        if (value == (byte)'\n')
        {
            Assert.True(result.IsComplete);
            Assert.Equal("hi"u8.ToArray(), result.Frame.ToArray());
            Assert.Equal(buffer.End, result.Consumed);
        }
        else
        {
            Assert.False(result.IsComplete);
        }
    }
}

[Fact]
public void Line_CrLfSplitAcrossSegments_TrimsCarriageReturn()
{
    var buffer = TestSequences.Create("hello"u8.ToArray().Concat(new[] { (byte)'\r' }).ToArray(),
                                     new[] { (byte)'\n' });
    var result = new LineFramer().Parse(buffer);

    Assert.True(result.IsComplete);
    Assert.Equal("hello"u8.ToArray(), result.Frame.ToArray());
    Assert.Equal(buffer.End, result.Consumed);
}

[Fact]
public void Line_MultipleLines_ReturnsFirstOnly()
{
    var buffer = new ReadOnlySequence<byte>("a\nb\n"u8.ToArray());
    var framer = new LineFramer();

    var first = framer.Parse(buffer);
    var second = framer.Parse(buffer.Slice(first.Consumed));

    Assert.Equal("a"u8.ToArray(), first.Frame.ToArray());
    Assert.Equal("b"u8.ToArray(), second.Frame.ToArray());
}

[Fact]
public void Line_EmptyLine_CompletesEmptyFrame()
{
    var buffer = new ReadOnlySequence<byte>("\n"u8.ToArray());
    var result = new LineFramer().Parse(buffer);

    Assert.True(result.IsComplete);
    Assert.True(result.Frame.IsEmpty);
    Assert.Equal(buffer.End, result.Consumed);
}

[Fact]
public void Line_ExceedsMaxWithoutSeparator_Throws()
{
    var framer = new LineFramer(new LineFramerOptions { MaxFrameSize = 4 });
    var buffer = new ReadOnlySequence<byte>("12345"u8.ToArray());

    Assert.Throws<InvalidDataException>(() => framer.Parse(buffer));
}

[Fact]
public void Line_WriteFrame_AppendsSeparator()
{
    var writer = new ArrayBufferWriter<byte>();
    new LineFramer().WriteFrame(writer, new ReadOnlySequence<byte>("x"u8.ToArray()));

    Assert.Equal("x\n"u8.ToArray(), writer.WrittenSpan.ToArray());
}
```

- [ ] 1.10 实现 `PassThroughFramer`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Framing/PassThroughFramer.cs`

```csharp
using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把当前非空 buffer 作为一帧；不保证多次写入不会被合并。</summary>
    public sealed class PassThroughFramer : IMessageFramer
    {
        public FrameParseResult Parse(in ReadOnlySequence<byte> buffer)
            => buffer.IsEmpty
                ? FrameParseResult.NeedMoreData(buffer)
                : FrameParseResult.Complete(buffer);

        public void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(writer);

            foreach (var segment in payload)
                writer.Write(segment.Span);
        }
    }
}
```

- [ ] 1.11 补齐透传与选项快照测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/FramingTests.cs`

```csharp
[Fact]
public void PassThrough_NonEmptyBuffer_ReturnsWholeBuffer()
{
    var buffer = new ReadOnlySequence<byte>("raw"u8.ToArray());
    var result = new PassThroughFramer().Parse(buffer);

    Assert.True(result.IsComplete);
    Assert.Equal("raw"u8.ToArray(), result.Frame.ToArray());
    Assert.Equal(buffer.End, result.Consumed);
}

[Fact]
public void PassThrough_EmptyBuffer_NeedsMoreData()
{
    var buffer = ReadOnlySequence<byte>.Empty;
    var result = new PassThroughFramer().Parse(buffer);

    Assert.False(result.IsComplete);
    Assert.Equal(buffer.Start, result.Consumed);
    Assert.Equal(buffer.End, result.Examined);
}

[Fact]
public void LengthPrefixed_OptionsAreSnapshotted()
{
    var options = new LengthFieldFramerOptions { MaxFrameSize = 16 };
    var framer = new LengthPrefixedFramer(options);
    options.MaxFrameSize = 1;

    var result = framer.Parse(new ReadOnlySequence<byte>(BuildLengthPrefixed(new byte[16])));

    Assert.True(result.IsComplete);
}

[Fact]
public void Line_OptionsAreSnapshotted()
{
    var options = new LineFramerOptions { Separator = (byte)'\n' };
    var framer = new LineFramer(options);
    options.Separator = (byte)';';

    var result = framer.Parse(new ReadOnlySequence<byte>("x\n"u8.ToArray()));

    Assert.True(result.IsComplete);
    Assert.Equal("x"u8.ToArray(), result.Frame.ToArray());
}
```

- [ ] 1.12 运行 focused framing 测试

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~FramingTests`
Expected: 全部 `FramingTests` 通过；无编译警告新增。

- [ ] 1.13 更新 Primitives README 的分帧章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“## 最小用法”之前增加：

````markdown
## 消息分帧

`IMessageFramer` 只负责“消息到哪结束”，不负责 payload ↔ T。首批提供：

| Framer | 边界 | 说明 |
|---|---|---|
| `LengthPrefixedFramer` | 4 字节大端长度 + payload | 默认 `MaxFrameSize` 1 MiB，零长度帧合法 |
| `LineFramer` | `\n`（可裁剪 `\r`） | 纯字节分隔符，UTF-8 解码交给 codec |
| `PassThroughFramer` | 当前非空 buffer | 兼容原始字节；不保证多次写入不被合并 |

```csharp
var framer = new LengthPrefixedFramer(
    new LengthFieldFramerOptions { MaxFrameSize = 1024 * 1024 });

var result = framer.Parse(buffer);
if (result.IsComplete)
{
    // 必须在 Frame 被消费前完成解码或拷贝。
    var payload = result.Frame;
    reader.AdvanceTo(result.Consumed, result.Examined);
}
```

帧不完整时 `Consumed == buffer.Start`、`Examined == buffer.End`；完整帧只消费一帧；
非法长度或超限抛 `InvalidDataException`，由 session 中止连接。
````

- [ ] 1.14 全量构建、测试与 OpenSpec 校验

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试 + 新增 `FramingTests` 全部通过。

Run: `openspec validate "primitives-message-framing" --strict --no-interactive`
Expected: `Change 'primitives-message-framing' is valid`。
