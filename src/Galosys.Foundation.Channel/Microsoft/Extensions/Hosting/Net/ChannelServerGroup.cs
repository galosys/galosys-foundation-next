using System.Collections.ObjectModel;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>按组合方式管理多个服务器的生命周期。</summary>
    /// <remarks>
    /// 组只负责编排调用顺序，不复制或改写每个 <see cref="IChannelServer"/> 的配置与状态；
    /// 调用方通过 <see cref="Servers"/> 读取各 server 的真实状态。<see cref="StartAsync"/> 与
    /// <see cref="StopAsync"/> 不承诺并发调用安全；<see cref="DisposeAsync"/> 对重复/并发调用幂等。
    /// </remarks>
    public sealed class ChannelServerGroup : IAsyncDisposable, IDisposable
    {
        private readonly IChannelServer[] _servers;
        private readonly TaskCompletionSource _disposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeStarted;

        /// <summary>创建服务器组；空数组合法。</summary>
        /// <param name="servers">按启动顺序排列的服务器。</param>
        /// <exception cref="ArgumentNullException">数组或任一元素为 null。</exception>
        public ChannelServerGroup(params IChannelServer[] servers)
        {
            ArgumentNullException.ThrowIfNull(servers);

            foreach (var server in servers)
                ArgumentNullException.ThrowIfNull(server);

            _servers = (IChannelServer[])servers.Clone();
            Servers = new ReadOnlyCollection<IChannelServer>(_servers);
        }

        /// <summary>构造时传入的服务器，顺序即启动顺序。</summary>
        public IReadOnlyList<IChannelServer> Servers { get; }

        /// <summary>按 <see cref="Servers"/> 顺序串行启动所有服务器。</summary>
        /// <remarks>
        /// 任一 server 启动失败（包括取消）时，本次调用中已启动的 server 会按相反顺序用
        /// <see cref="CancellationToken.None"/> 停止，随后原样重新抛出原始异常；
        /// 回滚失败只写入原始异常的 <see cref="Exception.Data"/>，不会覆盖原始异常。
        /// 未成功启动的 server 不会被停止。
        /// </remarks>
        public async Task StartAsync(CancellationToken ct)
        {
            var started = new List<IChannelServer>(_servers.Length);

            try
            {
                foreach (var server in _servers)
                {
                    ct.ThrowIfCancellationRequested();
                    await server.StartAsync(ct).ConfigureAwait(false);
                    started.Add(server);
                }
            }
            catch (Exception original)
            {
                var rollbackFailures = new List<Exception>();

                for (var i = started.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        await started[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackFailures.Add(rollbackError);
                    }
                }

                if (rollbackFailures.Count > 0)
                    original.Data["ChannelServerGroup.RollbackFailures"] = new AggregateException(rollbackFailures);

                throw;
            }
        }

        /// <summary>并行停止所有服务器，并把全部失败汇总为 <see cref="AggregateException"/>。</summary>
        /// <param name="ct">原样传给每个 server 的停止调用；按契约只取消等待，不取消停机流程。</param>
        public async Task StopAsync(CancellationToken ct)
        {
            var results = await Task.WhenAll(
                _servers.Select(server => StopOneAsync(server, ct))).ConfigureAwait(false);

            var failures = results
                .Where(error => error is not null)
                .Cast<Exception>()
                .ToArray();

            if (failures.Length > 0)
                throw new AggregateException("一个或多个服务器停止失败。", failures);
        }

        /// <summary>停止并释放所有服务器；重复或并发调用观察同一结果。</summary>
        /// <remarks>
        /// 第一次调用执行停止与释放，后续调用等待同一完成信号，不会重复释放。
        /// 任一 server 失败时仍会尝试其余 server，最终抛出包含全部失败的 <see cref="AggregateException"/>。
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            {
                var failures = new List<Exception>();

                try
                {
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (AggregateException error)
                {
                    failures.AddRange(error.InnerExceptions);
                }
                catch (Exception error)
                {
                    // StopAsync 目前只会抛 AggregateException；兜底保证后续释放与完成信号不被跳过。
                    failures.Add(error);
                }

                foreach (var server in _servers)
                {
                    try
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                if (failures.Count > 0)
                {
                    var aggregate = new AggregateException("一个或多个服务器释放失败。", failures);
                    _disposeCompleted.TrySetException(aggregate);
                    throw aggregate;
                }

                _disposeCompleted.TrySetResult();
            }

            await _disposeCompleted.Task.ConfigureAwait(false);
        }

        /// <summary>同步等待 <see cref="DisposeAsync"/> 完成。</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        private static async Task<Exception?> StopOneAsync(IChannelServer server, CancellationToken ct)
        {
            try
            {
                await server.StopAsync(ct).ConfigureAwait(false);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }
    }
}
