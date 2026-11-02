using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把当前非空 buffer 作为一帧；不保证多次写入不会被合并。</summary>
    public sealed class PassThroughFramer : IMessageFramer
    {
        /// <inheritdoc />
        public FrameParseResult Parse(in ReadOnlySequence<byte> buffer)
            => buffer.IsEmpty
                ? FrameParseResult.NeedMoreData(buffer)
                : FrameParseResult.Complete(buffer);

        /// <inheritdoc />
        public void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(writer);

            foreach (var segment in payload)
                writer.Write(segment.Span);
        }
    }
}
