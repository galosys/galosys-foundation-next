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

        // Utf8JsonReader 在默认 AllowMultipleValues = false 时抛 JsonReaderException（JsonException 的子类）。
        Assert.ThrowsAny<JsonException>(() => codec.Decode(frame));
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
}
