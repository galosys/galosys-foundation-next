using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Channels = System.Threading.Channels;

namespace Galosys.Foundation.Channel
{
    /// <summary>有界客户端连接池；复用健康连接，池满时等待归还。</summary>
    public sealed class ChannelClientPool : IAsyncDisposable
    {
        private readonly ChannelClientPoolOptions _options;
        private readonly IChannelClientFactory _factory;
        private readonly Channels.Channel<IChannelClient> _idle = Channels.Channel.CreateUnbounded<IChannelClient>();
        private readonly SemaphoreSlim _available = new(0);
        private readonly ConcurrentDictionary<IChannelClient, byte> _rented = new();
        private readonly CancellationTokenSource _disposeCts = new();
        private int _total;
        private int _disposed;

        /// <summary>创建连接池。</summary>
        /// <param name="options">池配置；构造时会校验并克隆。</param>
        /// <param name="factory">客户端工厂；null 时使用 <see cref="ChannelClientFactory"/>。</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
        /// <exception cref="ArgumentException">配置无效。</exception>
        public ChannelClientPool(
            ChannelClientPoolOptions options,
            IChannelClientFactory? factory = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            options.Validate();
            _options = options.Clone();
            _factory = factory ?? new ChannelClientFactory();
        }

        /// <summary>租借一个已连接的客户端；池满时最多等待 <see cref="ChannelClientPoolOptions.RentTimeout"/>。</summary>
        /// <exception cref="TimeoutException">等待归还超时。</exception>
        /// <exception cref="OperationCanceledException">调用方取消。</exception>
        /// <exception cref="ObjectDisposedException">连接池已释放。</exception>
        public async ValueTask<IChannelClient> RentAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            var deadline = DateTime.UtcNow + _options.RentTimeout;

            while (true)
            {
                ThrowIfDisposed();

                if (_idle.Reader.TryRead(out var idle))
                {
                    if (IsHealthy(idle))
                        return Rent(idle);

                    await DisposeClientAsync(idle).ConfigureAwait(false);
                    ReleaseSlot();
                    continue;
                }

                if (TryReserveSlot())
                {
                    IChannelClient? client = null;

                    try
                    {
                        client = _factory.Create(_options.ClientOptions);
                        await client.ConnectAsync(ct).ConfigureAwait(false);
                        return Rent(client);
                    }
                    catch
                    {
                        if (client is not null)
                            await DisposeClientAsync(client).ConfigureAwait(false);

                        ReleaseSlot();
                        throw;
                    }
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("连接池在 RentTimeout 内没有可用连接。");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);

                try
                {
                    if (!await _available.WaitAsync(remaining, linked.Token).ConfigureAwait(false))
                        throw new TimeoutException("连接池在 RentTimeout 内没有可用连接。");
                }
                catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(ChannelClientPool));
                }
            }
        }

        /// <summary>归还客户端；归还后调用方不得继续使用。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
        /// <exception cref="InvalidOperationException">客户端不属于本池或已归还。</exception>
        public async ValueTask ReturnAsync(IChannelClient client)
        {
            ArgumentNullException.ThrowIfNull(client);

            if (!_rented.TryRemove(client, out _))
                throw new InvalidOperationException("该客户端不属于当前连接池或已归还。");

            if (Volatile.Read(ref _disposed) != 0 || !IsHealthy(client))
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
                ReleaseSlot();
                WakeWaiter();
                return;
            }

            if (!_idle.Writer.TryWrite(client))
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
                ReleaseSlot();
            }

            WakeWaiter();
        }

        /// <summary>释放空闲客户端；已租出的客户端在归还时释放。</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _disposeCts.Cancel();
            _idle.Writer.TryComplete();

            while (_idle.Reader.TryRead(out var client))
                await DisposeClientAsync(client).ConfigureAwait(false);
        }

        private IChannelClient Rent(IChannelClient client)
        {
            if (!_rented.TryAdd(client, 0))
                throw new InvalidOperationException("客户端已在租借中。");

            return client;
        }

        private bool TryReserveSlot()
        {
            while (true)
            {
                var current = Volatile.Read(ref _total);
                if (current >= _options.MaxSize)
                    return false;

                if (Interlocked.CompareExchange(ref _total, current + 1, current) == current)
                    return true;
            }
        }

        private void ReleaseSlot()
        {
            if (Interlocked.Decrement(ref _total) < 0)
                Interlocked.Exchange(ref _total, 0);
        }

        private void WakeWaiter()
        {
            try
            {
                _available.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private static bool IsHealthy(IChannelClient client)
            => client.State == ChannelConnectionState.Connected && client.Connection is not null;

        private static async ValueTask DisposeClientAsync(IChannelClient client)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ChannelClientPool));
        }
    }
}
