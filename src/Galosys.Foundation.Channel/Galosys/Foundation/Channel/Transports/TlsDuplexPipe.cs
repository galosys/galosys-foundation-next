using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel
{
    /// <summary>
    /// 认证后的 TLS 双向管道：握手成功后把明文数据在解密输入管道与加密输出管道之间搬运。
    /// 该适配器不拥有底层传输，底层连接仍由 <c>ChannelServer</c> / <c>ChannelClient</c> 释放。
    /// </summary>
    internal sealed class TlsDuplexPipe : IDuplexPipe, IAsyncDisposable
    {
        // 释放发生在连接收尾的关键路径上，等待窗口必须短：
        // 超过该时间仍未排空的数据一律丢弃，由底层连接负责收尾。
        private static readonly TimeSpan WriteDrainTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReadStopTimeout = TimeSpan.FromSeconds(1);
        private const int ReadSegmentSize = 4096;

        private readonly IDuplexPipe _inner;
        private readonly SslStream _ssl;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _abort = new();
        private readonly Task _readPump;
        private readonly Task _writePump;
        private readonly TaskCompletionSource _disposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _disposed;
        private int _innerOutputCompleted;
        private volatile bool _shuttingDown;

        private TlsDuplexPipe(IDuplexPipe inner, SslStream ssl)
        {
            _inner = inner;
            _ssl = ssl;
            _readPump = ReadPumpAsync(_abort.Token);
            _writePump = WritePumpAsync(_abort.Token);
        }

        /// <summary>解密后的输入管道。</summary>
        public PipeReader Input => _input.Reader;

        /// <summary>待加密发送的输出管道。</summary>
        public PipeWriter Output => _output.Writer;

        /// <summary>
        /// 在既有 <see cref="IDuplexPipe"/> 上完成 TLS 握手，并返回可读写的认证后管道。
        /// 握手失败时不会暴露任何可用传输：<see cref="SslStream"/> 会被释放并抛出异常。
        /// </summary>
        /// <param name="inner">底层明文传输；本适配器不负责释放它。</param>
        /// <param name="options">TLS 配置。</param>
        /// <param name="isServer">true 表示服务端握手，false 表示客户端握手。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public static async ValueTask<TlsDuplexPipe> CreateAsync(
            IDuplexPipe inner,
            ChannelTlsOptions options,
            bool isServer,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(options);

            // 校验必须先于握手，避免用无效配置进入 TLS 协商。
            if (isServer)
                options.ValidateServer();
            else
                options.ValidateClient();

            var ssl = new SslStream(new SslStreamDuplexPipe(inner), leaveInnerStreamOpen: false);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(options.HandshakeTimeout);

            try
            {
                if (isServer)
                {
                    await ssl.AuthenticateAsServerAsync(options.ServerOptions!, handshakeCts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ssl.AuthenticateAsClientAsync(options.ClientOptions!, handshakeCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // 握手失败：释放 SslStream，不暴露可用传输，也不回退明文。
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new TlsDuplexPipe(inner, ssl);
        }

        /// <summary>
        /// 读泵：把 <see cref="SslStream"/> 解密后的字节搬到解密输入管道。
        /// 底层输入 EOF 只结束解密输入，不影响写方向（半关闭）。
        /// </summary>
        private async Task ReadPumpAsync(CancellationToken cancellationToken)
        {
            Exception? error = null;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var memory = _input.Writer.GetMemory(ReadSegmentSize);
                    var read = await _ssl.ReadAsync(memory, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                        break;

                    _input.Writer.Advance(read);
                    var flush = await _input.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flush.IsCompleted)
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                await _input.Writer.CompleteAsync(error).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 写泵：把输出管道中已提交的明文字节加密写回底层传输。
        /// 输出完成后先发送 TLS <c>close_notify</c>，再完成底层输出，让底层发送循环走 FIN。
        /// </summary>
        private async Task WritePumpAsync(CancellationToken cancellationToken)
        {
            Exception? error = null;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                    try
                    {
                        foreach (var segment in result.Buffer)
                            await _ssl.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _output.Reader.AdvanceTo(result.Buffer.End);
                    }

                    // 已提交的字节必须先冲刷出去，再发送 close_notify。
                    await _ssl.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (result.IsCompleted || result.IsCanceled)
                        break;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    // ShutdownAsync 只写 close_notify、不等待对端，可安全用于半关闭。
                    await _ssl.ShutdownAsync().ConfigureAwait(false);
                    CompleteInnerOutput(null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 框架主动释放属于正常收尾，不应把底层连接标记为异常终止。
                if (!_shuttingDown)
                    error = new IOException("TLS 写泵已取消。");
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // 无论正常结束、取消还是异常，都必须完成底层输出，否则底层发送循环会挂到超时。
                CompleteInnerOutput(error);
                await _output.Reader.CompleteAsync(error).ConfigureAwait(false);
            }
        }

        /// <summary>完成底层输出管道（只完成，不释放）。</summary>
        private void CompleteInnerOutput(Exception? error)
        {
            if (Interlocked.Exchange(ref _innerOutputCompleted, 1) != 0)
                return;

            try
            {
                _inner.Output.Complete(error);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 释放适配器：先完成对外输出并有界等待写泵排空，再取消读泵、释放 <see cref="SslStream"/> 与内部管道。
        /// 幂等；重复调用等待同一个释放任务完成。
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return new ValueTask(_disposeCompleted.Task);

            return new ValueTask(DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                // 标记为正常收尾：写泵因取消而结束时不应把底层连接标记为异常终止。
                _shuttingDown = true;

                // 先完成对外暴露的输出，让写泵把已提交的数据排空。
                await _output.Writer.CompleteAsync().ConfigureAwait(false);

                // 取消内部 CTS，让读泵从 SslStream.ReadAsync 中退出。
                try
                {
                    _abort.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                // 读泵与写泵并行等待，最坏耗时是两者之一而不是相加。
                await Task.WhenAll(
                    WaitPumpAsync(_writePump, WriteDrainTimeout),
                    WaitPumpAsync(_readPump, ReadStopTimeout)).ConfigureAwait(false);

                await _ssl.DisposeAsync().ConfigureAwait(false);

                await _input.Writer.CompleteAsync().ConfigureAwait(false);
                await _output.Reader.CompleteAsync().ConfigureAwait(false);
                _input.Reader.Complete();

                _disposeCompleted.TrySetResult();
            }
            catch (Exception ex)
            {
                // 失败也要让等待者看到同一个异常，而不是被后续的 TrySetResult 覆盖。
                _disposeCompleted.TrySetException(ex);
                throw;
            }
            finally
            {
                // 已获取的 CancellationToken 在源释放后仍可安全使用，
                // 因此这里释放 CTS 不会影响可能仍在运行的读泵。
                _abort.Dispose();
            }
        }

        /// <summary>有界等待一个泵结束；超时按放弃剩余数据处理，不视为失败。</summary>
        private static async Task WaitPumpAsync(Task pump, TimeSpan timeout)
        {
            try
            {
                await pump.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 超时：剩余数据丢弃，由底层连接负责收尾。
            }
            catch (Exception)
            {
                // 泵自身的异常已在泵内处理，这里只负责收尾。
            }
        }
    }
}
