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

        /// <summary>创建换行分帧器；选项在构造时快照。</summary>
        /// <param name="options">分帧配置；为 null 时使用默认值。</param>
        /// <exception cref="ArgumentOutOfRangeException">MaxFrameSize 不大于 0。</exception>
        public LineFramer(LineFramerOptions? options = null)
        {
            options ??= new LineFramerOptions();
            if (options.MaxFrameSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxFrameSize 必须大于 0。");

            _maxFrameSize = options.MaxFrameSize;
            _separator = options.Separator;
            _trimCarriageReturn = options.TrimCarriageReturn;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidDataException">未找到分隔符且缓冲区超过 MaxFrameSize，或帧长度超过上限。</exception>
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

            // GetOffset 不是相对序列起点的零基偏移；切片后的序列必须减去起点偏移。
            var separatorIndex = buffer.GetOffset(separatorPosition.Value) - buffer.GetOffset(buffer.Start);
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

        /// <inheritdoc />
        /// <exception cref="InvalidDataException">payload 超过 MaxFrameSize。</exception>
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
    }
}
