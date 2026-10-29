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

        /// <summary>把一帧 UTF-8 payload 解码为字符串。</summary>
        /// <exception cref="InvalidDataException">帧为空。</exception>
        /// <exception cref="DecoderFallbackException">帧包含非法 UTF-8 字节。</exception>
        public string Decode(ReadOnlySequence<byte> frame)
        {
            if (frame.IsEmpty)
                throw new InvalidDataException("UTF-8 文本帧不能为空。");

            return frame.IsSingleSegment
                ? Utf8.GetString(frame.FirstSpan)
                : Utf8.GetString(frame.ToArray());
        }

        /// <summary>把字符串编码为 UTF-8 payload；不追加终止符或分隔符。</summary>
        /// <exception cref="ArgumentNullException">writer 或 message 为 null。</exception>
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
