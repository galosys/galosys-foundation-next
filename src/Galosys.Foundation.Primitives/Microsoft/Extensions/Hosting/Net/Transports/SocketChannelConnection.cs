using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于 socket 与 <see cref="Pipe"/> 的连接实现。</summary>
    internal sealed class SocketChannelConnection : IChannelConnection
    {
        private static readonly TimeSpan CloseDrainTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan InboundDrainTimeout = TimeSpan.FromSeconds(1);
        private const int MinimumSegmentSize = 4096;

        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly Pipe _input;
        private readonly Pipe _output;
        private readonly IDuplexPipe _transport;
        private readonly CancellationTokenSource _abortCts = new();
        private readonly TaskCompletionSource<Exception?> _termination =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _receiveLoop;
        private readonly Task _sendLoop;
        private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ILogger? _logger;
        private readonly int _readSegmentSize;

        private Exception? _abortReason;
        private int _aborted;
        private int _disposed;
        private int _inputWriterCompleted;
        private int _outputWriterCompleted;
        private int _outputReaderCompleted;
        private int _inputReaderCompleted;

        public SocketChannelConnection(Socket socket, ChannelServerOptions options, ILogger? logger)
        {
            _socket = socket;
            _logger = logger;
            LocalEndPoint = socket.LocalEndPoint;
            RemoteEndPoint = socket.RemoteEndPoint;
            _stream = new NetworkStream(socket, ownsSocket: false);

            var readBufferSize = options.MaxReadBufferSize ?? 1024 * 1024;
            var writeBufferSize = options.MaxWriteBufferSize ?? 64 * 1024;
            _readSegmentSize = (int)Math.Min(MinimumSegmentSize, readBufferSize);

            _input = new Pipe(CreatePipeOptions(readBufferSize, _readSegmentSize));
            _output = new Pipe(CreatePipeOptions(
                writeBufferSize,
                (int)Math.Min(MinimumSegmentSize, writeBufferSize)));
            _transport = new DuplexPipe(_input.Reader, _output.Writer);

            _receiveLoop = ReceiveLoopAsync(_abortCts.Token);
            _sendLoop = SendLoopAsync(_abortCts.Token);
        }

        public EndPoint? LocalEndPoint { get; }

        public EndPoint? RemoteEndPoint { get; }

        public IDuplexPipe Transport => _transport;

        public Task<Exception?> Termination => _termination.Task;

        private bool IsAborted => Volatile.Read(ref _aborted) != 0;

        public void Abort(Exception? reason)
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0)
            {
                if (reason is not null)
                    Interlocked.CompareExchange(ref _abortReason, reason, null);

                return;
            }

            if (reason is not null)
                Interlocked.CompareExchange(ref _abortReason, reason, null);

            SignalTermination(reason);
            var error = CreateAbortException();

            // 先以异常完成管道，避免取消回调先一步把读取方当作正常结束。
            CompleteInputWriter(error);
            CompleteOutputWriter(error);
            CompleteOutputReader(error);

            try
            {
                _abortCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await _disposeCompleted.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                if (!IsAborted)
                {
                    // 正常收尾时先完成输出，让发送循环把已提交的数据写完。
                    CompleteOutputWriter(null);
                    await WaitForSendDrainAsync().ConfigureAwait(false);
                }

                if (!IsAborted)
                {
                    // 先停接收循环，再直接读空内核接收缓冲区，避免带未读入站数据关闭触发 RST。
                    await StopReceiveLoopAsync().ConfigureAwait(false);
                }

                if (!IsAborted)
                {
                    await DrainInboundAsync().ConfigureAwait(false);
                }

                try
                {
                    _abortCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    _socket.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }

                await WaitLoopAsync(_sendLoop).ConfigureAwait(false);
                await WaitLoopAsync(_receiveLoop).ConfigureAwait(false);

                CompleteInputWriter(IsAborted ? CreateAbortException() : null);
                CompleteOutputReader(null);
                CompleteInputReader();

                try
                {
                    _stream.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }

                _abortCts.Dispose();
            }
            catch (Exception ex)
            {
                _disposeCompleted.TrySetException(ex);
                throw;
            }
            finally
            {
                _disposeCompleted.TrySetResult();
            }
        }

        private async Task WaitForSendDrainAsync()
        {
            using var drainCts = new CancellationTokenSource();
            var drainTimeout = Task.Delay(CloseDrainTimeout, drainCts.Token);
            var completed = await Task.WhenAny(_sendLoop, drainTimeout).ConfigureAwait(false);

            if (completed == _sendLoop)
            {
                drainCts.Cancel();
                return;
            }

            Abort(new TimeoutException(
                $"等待连接发送缓冲排空超过 {CloseDrainTimeout}。"));
        }

        private async Task StopReceiveLoopAsync()
        {
            try
            {
                _abortCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await _receiveLoop.WaitAsync(CloseDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Abort(new TimeoutException("等待连接接收循环退出超时。"));
            }
            catch (Exception)
            {
            }
        }

        private async Task DrainInboundAsync()
        {
            using var drainCts = new CancellationTokenSource(InboundDrainTimeout);
            var buffer = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                while (!IsAborted)
                {
                    int read;

                    try
                    {
                        read = await _stream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            drainCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (read == 0)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static PipeOptions CreatePipeOptions(long pauseWriterThreshold, int minimumSegmentSize)
        {
            // 恢复阈值必须不大于暂停阈值；段大小也不能超过暂停阈值。
            var resumeWriterThreshold = pauseWriterThreshold / 2;

            return new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.ThreadPool,
                writerScheduler: PipeScheduler.ThreadPool,
                pauseWriterThreshold: pauseWriterThreshold,
                resumeWriterThreshold: resumeWriterThreshold,
                minimumSegmentSize: minimumSegmentSize,
                useSynchronizationContext: false);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            Exception? error = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var memory = _input.Writer.GetMemory(_readSegmentSize);
                    var read = await _stream.ReadAsync(memory, ct).ConfigureAwait(false);

                    if (read == 0)
                        break;

                    _input.Writer.Advance(read);

                    var flush = await _input.Writer.FlushAsync(ct).ConfigureAwait(false);
                    if (flush.IsCompleted)
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // 主动关闭/中止期间 socket 被释放，读取异常不代表对端重置。
            }
            catch (Exception ex)
            {
                error = ex;
                _logger?.LogDebug(ex, "连接接收循环因异常结束。");
            }
            finally
            {
                if (error is not null)
                    SignalTermination(error);

                CompleteInputWriter(error);
            }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            Exception? error = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _output.Reader.ReadAsync(ct).ConfigureAwait(false);
                    var buffer = result.Buffer;

                    try
                    {
                        while (buffer.Length > 0)
                        {
                            var memory = buffer.First;
                            await _stream.WriteAsync(memory, ct).ConfigureAwait(false);
                            buffer = buffer.Slice(memory.Length);
                        }
                    }
                    finally
                    {
                        _output.Reader.AdvanceTo(result.Buffer.End);
                    }

                    if (result.IsCompleted || result.IsCanceled)
                        break;
                }

                if (!ct.IsCancellationRequested)
                {
                    await _stream.FlushAsync(ct).ConfigureAwait(false);

                    try
                    {
                        _socket.Shutdown(SocketShutdown.Send);
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = ex;
                _logger?.LogDebug(ex, "连接发送循环因异常结束。");
            }
            finally
            {
                if (error is not null)
                    SignalTermination(error);

                CompleteOutputReader(error);
            }
        }

        private void SignalTermination(Exception? error)
            => _termination.TrySetResult(error);

        private IOException CreateAbortException()
            => _abortReason is null
                ? new IOException("连接已中止。")
                : new IOException("连接已中止。", _abortReason);

        private void CompleteInputWriter(Exception? error)
        {
            if (Interlocked.Exchange(ref _inputWriterCompleted, 1) != 0)
                return;

            try
            {
                _input.Writer.Complete(error);
            }
            catch (Exception)
            {
            }
        }

        private void CompleteOutputWriter(Exception? error)
        {
            if (Interlocked.Exchange(ref _outputWriterCompleted, 1) != 0)
                return;

            try
            {
                _output.Writer.Complete(error);
            }
            catch (Exception)
            {
            }
        }

        private void CompleteOutputReader(Exception? error)
        {
            if (Interlocked.Exchange(ref _outputReaderCompleted, 1) != 0)
                return;

            try
            {
                _output.Reader.Complete(error);
            }
            catch (Exception)
            {
            }
        }

        private void CompleteInputReader()
        {
            if (Interlocked.Exchange(ref _inputReaderCompleted, 1) != 0)
                return;

            try
            {
                _input.Reader.Complete();
            }
            catch (Exception)
            {
            }
        }

        private static async Task WaitLoopAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}
