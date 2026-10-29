using System.IO;
using System.IO.Pipelines;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>借用连接的拉模型消息会话；实现细节不对外公开。</summary>
    internal sealed class ChannelSession<T> : IChannelSession<T>
    {
        private readonly ChannelContext _context;
        private readonly IMessageFramer _framer;
        private readonly IMessageCodec<T> _codec;
        private readonly int _maxMessageSize;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _reading;
        private int _disposed;

        internal ChannelSession(
            ChannelContext context,
            IMessageFramer framer,
            IMessageCodec<T> codec,
            MessageChannelOptions options)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(framer);
            ArgumentNullException.ThrowIfNull(codec);
            ArgumentNullException.ThrowIfNull(options);

            if (options.MaxMessageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxMessageSize 必须大于 0。");

            _context = context;
            _framer = framer;
            _codec = codec;
            _maxMessageSize = options.MaxMessageSize;

            if (!context.TrySetSession(this))
            {
                _writeLock.Dispose();
                throw new InvalidOperationException("每个连接只能创建一个会话。");
            }
        }

        public async ValueTask<T> ReadAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                throw new InvalidOperationException("同一个会话不允许并发读取。");

            try
            {
                while (true)
                {
                    var result = await _context.Reader.ReadAsync(ct).ConfigureAwait(false);
                    var buffer = result.Buffer;

                    if (buffer.IsEmpty && result.IsCompleted)
                        throw new EndOfStreamException();

                    FrameParseResult parse;
                    try
                    {
                        parse = _framer.Parse(buffer);
                    }
                    catch (Exception ex)
                    {
                        _context.Abort(ex);
                        throw;
                    }

                    if (parse.IsComplete)
                    {
                        if (parse.Frame.Length > _maxMessageSize)
                        {
                            var ex = new InvalidDataException(
                                $"消息长度 {parse.Frame.Length} 超过上限 {_maxMessageSize}。");
                            _context.Abort(ex);
                            throw ex;
                        }

                        T message;
                        try
                        {
                            message = _codec.Decode(parse.Frame);
                        }
                        catch (Exception ex)
                        {
                            _context.Abort(ex);
                            throw;
                        }

                        _context.Reader.AdvanceTo(parse.Consumed, parse.Examined);
                        return message;
                    }

                    if (result.IsCompleted)
                    {
                        var ex = new InvalidDataException("连接在帧中途结束。");
                        _context.Abort(ex);
                        throw ex;
                    }

                    _context.Reader.AdvanceTo(parse.Consumed, parse.Examined);
                }
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        }

        public async ValueTask WriteAsync(T message, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                using var buffer = new PooledBufferWriter();
                _codec.Encode(buffer, message);

                if (buffer.WrittenMemory.Length > _maxMessageSize)
                    throw new InvalidDataException(
                        $"消息长度 {buffer.WrittenMemory.Length} 超过上限 {_maxMessageSize}。");

                try
                {
                    _framer.WriteFrame(_context.Writer, buffer.WrittenSequence);
                }
                catch (Exception ex)
                {
                    _context.Abort(ex);
                    throw;
                }

                FlushResult flush;
                try
                {
                    flush = await _context.Writer.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _context.Abort(ex);
                    throw;
                }

                if (flush.IsCompleted)
                    throw new IOException("连接已关闭，无法继续写入。");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _writeLock.WaitAsync().ConfigureAwait(false);
            _writeLock.Release();

            // 不 Dispose SemaphoreSlim：可能仍有已通过 disposed 检查的等待者；
            // 它不持有非托管句柄，保留到 GC 是安全的。
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ChannelSession<T>));
        }
    }
}
