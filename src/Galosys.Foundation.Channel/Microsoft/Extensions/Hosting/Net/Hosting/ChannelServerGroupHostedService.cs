using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把服务器组映射到宿主生命周期。</summary>
    /// <remarks>
    /// 只转发启动、停机和释放，不改变组自身的顺序、回滚、聚合与幂等语义。
    /// </remarks>
    public sealed class ChannelServerGroupHostedService : IHostedService, IAsyncDisposable
    {
        private readonly ChannelServerGroup _group;

        /// <summary>创建 hosted service。</summary>
        /// <param name="group">要托管的服务器组。</param>
        /// <exception cref="ArgumentNullException"><paramref name="group"/> 为 null。</exception>
        public ChannelServerGroupHostedService(ChannelServerGroup group)
        {
            ArgumentNullException.ThrowIfNull(group);
            _group = group;
        }

        /// <summary>按组顺序启动所有服务器。</summary>
        public Task StartAsync(CancellationToken cancellationToken)
            => _group.StartAsync(cancellationToken);

        /// <summary>停止组内所有服务器；传入 <see cref="CancellationToken.None"/> 以尊重各 server 的停机超时。</summary>
        public Task StopAsync(CancellationToken cancellationToken)
            => _group.StopAsync(CancellationToken.None);

        /// <summary>释放组；组自身保证幂等。</summary>
        public ValueTask DisposeAsync() => _group.DisposeAsync();
    }
}
