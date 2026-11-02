using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

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

    [Fact]
    public void WriteFrame_AboveMax_Throws()
    {
        var payload = new ReadOnlySequence<byte>(new byte[5]);

        Assert.Throws<InvalidDataException>(() =>
            new LengthPrefixedFramer(new LengthFieldFramerOptions { MaxFrameSize = 4 })
                .WriteFrame(new ArrayBufferWriter<byte>(), payload));
        Assert.Throws<InvalidDataException>(() =>
            new LineFramer(new LineFramerOptions { MaxFrameSize = 4 })
                .WriteFrame(new ArrayBufferWriter<byte>(), payload));
    }

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

    [Fact]
    public void Line_CustomSeparator_SplitsOnSeparator()
    {
        var framer = new LineFramer(new LineFramerOptions { Separator = (byte)';' });
        var buffer = new ReadOnlySequence<byte>("a;b;"u8.ToArray());

        var first = framer.Parse(buffer);
        var second = framer.Parse(buffer.Slice(first.Consumed));

        Assert.Equal("a"u8.ToArray(), first.Frame.ToArray());
        Assert.Equal("b"u8.ToArray(), second.Frame.ToArray());
        Assert.Equal(buffer.End, second.Consumed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FramerConstructors_Throw_WhenMaxFrameSizeNotPositive(int maxFrameSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LengthPrefixedFramer(new LengthFieldFramerOptions { MaxFrameSize = maxFrameSize }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LineFramer(new LineFramerOptions { MaxFrameSize = maxFrameSize }));
    }

    private static byte[] BuildLengthPrefixed(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }
}

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
