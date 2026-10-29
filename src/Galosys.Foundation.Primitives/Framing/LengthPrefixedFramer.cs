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

        /// <summary>创建长度前缀分帧器；选项在构造时快照。</summary>
        /// <param name="options">分帧配置；为 null 时使用默认值。</param>
        /// <exception cref="ArgumentOutOfRangeException">MaxFrameSize 不大于 0。</exception>
        public LengthPrefixedFramer(LengthFieldFramerOptions? options = null)
        {
            options ??= new LengthFieldFramerOptions();
            if (options.MaxFrameSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxFrameSize 必须大于 0。");

            _maxFrameSize = options.MaxFrameSize;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        /// <exception cref="InvalidDataException">payload 超过 MaxFrameSize 或超出长度字段表示范围。</exception>
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
    }
}
