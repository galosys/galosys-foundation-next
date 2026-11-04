using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Galosys.Foundation.Channel
{
    /// <summary>
    /// 把 <see cref="IDuplexPipe"/> 适配成 <see cref="SslStream"/> 需要的只读/只写 <see cref="Stream"/>。
    /// 该适配器不拥有底层管道：底层连接仍由 <c>ChannelServer</c> / <c>ChannelClient</c> 收尾。
    /// </summary>
    internal sealed class SslStreamDuplexPipe : Stream
    {
        private readonly IDuplexPipe _pipe;

        /// <summary>创建适配器。</summary>
        /// <param name="pipe">底层双向管道；本适配器不负责释放它。</param>
        public SslStreamDuplexPipe(IDuplexPipe pipe)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// 从底层输入管道读取字节。返回 0 表示底层输入已结束（EOF）。
        /// </summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
                return 0;

            while (true)
            {
                var result = await _pipe.Input.ReadAsync(cancellationToken).ConfigureAwait(false);

                if (result.IsCanceled)
                    throw new OperationCanceledException(cancellationToken);

                if (!result.Buffer.IsEmpty)
                {
                    // 只拷贝调用方缓冲区能容纳的部分，并且精确消费掉拷贝的字节数。
                    // 调用方缓冲区非空且底层有数据，因此 count 必然大于 0。
                    var count = (int)Math.Min(result.Buffer.Length, buffer.Length);
                    CopyTo(result.Buffer, buffer, count);
                    _pipe.Input.AdvanceTo(result.Buffer.GetPosition(count));

                    return count;
                }

                if (result.IsCompleted)
                    return 0;

                // 缓冲区为空但尚未结束：继续等待，不能把 0 当成 EOF 交给 SslStream。
                _pipe.Input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }
        }

        /// <summary>
        /// 把序列的前 <paramref name="count"/> 个字节拷贝到目标内存，保持字节顺序。
        /// </summary>
        private static void CopyTo(ReadOnlySequence<byte> source, Memory<byte> destination, int count)
        {
            var offset = 0;

            foreach (var segment in source)
            {
                if (offset >= count)
                    break;

                var length = Math.Min(segment.Length, count - offset);
                segment.Slice(0, length).CopyTo(destination.Slice(offset, length));
                offset += length;
            }
        }

        /// <summary>
        /// 写入字节并立即刷新底层输出。握手阶段必须立即刷新，否则握手字节会滞留在管道里导致双方互等。
        /// </summary>
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _pipe.Output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _pipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken)
            => _pipe.Output.FlushAsync(cancellationToken).AsTask();

        /// <summary>
        /// 同步刷新。SslStream 在部分内部路径（例如发送告警）上会调用同步 API，因此这里必须可用。
        /// </summary>
        public override void Flush()
            => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>
        /// 同步读取。SslStream 在部分内部路径上会调用同步 API，因此这里必须可用。
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || count < 0 || buffer.Length - offset < count)
                throw new ArgumentOutOfRangeException(nameof(offset));

            if (count == 0)
                return 0;

            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value)
            => throw new NotSupportedException();

        /// <summary>
        /// 同步写入。SslStream 在部分内部路径（例如发送告警）上会调用同步 API，因此这里必须可用。
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || count < 0 || buffer.Length - offset < count)
                throw new ArgumentOutOfRangeException(nameof(offset));

            if (count == 0)
                return;

            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        /// <summary>释放适配器自身；底层管道由框架释放，因此这里不做任何管道收尾。</summary>
        protected override void Dispose(bool disposing)
        {
            // 底层管道由 ChannelServer / ChannelClient 释放。
            base.Dispose(disposing);
        }
    }
}
