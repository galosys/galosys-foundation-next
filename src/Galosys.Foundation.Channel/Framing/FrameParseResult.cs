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

        /// <summary>是否已得到一帧完整 payload。</summary>
        public bool IsComplete { get; }

        /// <summary>完整帧的 payload；不完整时为默认值。</summary>
        public ReadOnlySequence<byte> Frame { get; }

        /// <summary>调用方可以安全消费到的位置。</summary>
        public SequencePosition Consumed { get; }

        /// <summary>本次解析实际检查到的位置。</summary>
        public SequencePosition Examined { get; }

        /// <summary>表示需要更多数据，不消费当前缓冲区的任何字节。</summary>
        public static FrameParseResult NeedMoreData(in ReadOnlySequence<byte> buffer)
            => new(false, default, buffer.Start, buffer.End);

        /// <summary>表示需要更多数据，并显式指定已消费与已检查位置。</summary>
        public static FrameParseResult NeedMoreData(SequencePosition consumed, SequencePosition examined)
            => new(false, default, consumed, examined);

        /// <summary>表示已得到完整帧，消费位置指向帧末尾。</summary>
        public static FrameParseResult Complete(ReadOnlySequence<byte> frame)
            => new(true, frame, frame.End, frame.End);

        /// <summary>表示已得到完整帧，并显式指定消费位置。</summary>
        public static FrameParseResult Complete(ReadOnlySequence<byte> frame, SequencePosition consumed)
            => new(true, frame, consumed, consumed);
    }
}
