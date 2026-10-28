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
