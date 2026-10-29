using System.Buffers;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 ArrayPool 的可增长 writer，用于先编码 payload 再加帧头。</summary>
    internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
        private int _written;

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public ReadOnlySequence<byte> WrittenSequence => new(_buffer, 0, _written);

        public void Advance(int count)
        {
            if (count < 0 || checked(_written + count) > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;

            var required = checked(_written + sizeHint);
            if (required <= _buffer.Length)
                return;

            var next = ArrayPool<byte>.Shared.Rent(Math.Max(required, checked(_buffer.Length * 2)));
            _buffer.AsSpan(0, _written).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = Array.Empty<byte>();

            if (buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
