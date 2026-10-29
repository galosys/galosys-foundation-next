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
