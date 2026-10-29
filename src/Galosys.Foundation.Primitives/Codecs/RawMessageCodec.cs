using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>原始字节编解码器；Decode 总是返回独立拷贝。</summary>
    public sealed class RawMessageCodec : IMessageCodec<ReadOnlyMemory<byte>>
    {
        /// <summary>解码一帧原始字节；返回值不依赖原缓冲区，可在 AdvanceTo 之后继续使用。</summary>
        public ReadOnlyMemory<byte> Decode(ReadOnlySequence<byte> frame)
            => frame.IsSingleSegment
                ? frame.First.ToArray()
                : frame.ToArray();

        /// <summary>把原始字节写入目标缓冲区；不添加长度头或分隔符。</summary>
        /// <exception cref="ArgumentNullException">writer 为 null。</exception>
        public void Encode(IBufferWriter<byte> writer, ReadOnlyMemory<byte> message)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.Write(message.Span);
        }
    }
}
