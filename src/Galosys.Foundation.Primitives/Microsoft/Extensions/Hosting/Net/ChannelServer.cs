using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>基于组合的服务器入口。</summary>
    public sealed class ChannelServer : IChannelServer
    {
        private readonly ChannelServerCore _core;

        /// <summary>创建组合式服务器。</summary>
        /// <remarks>
        /// <paramref name="configurePipeline"/> 在每次启动尝试时执行一次，必须调用
        /// <see cref="IChannelPipeline.Run"/>；否则启动失败且不会绑定监听器。
        /// </remarks>
        public ChannelServer(
            ChannelServerOptions options,
            Action<IChannelPipeline> configurePipeline,
            ILoggerFactory? loggerFactory = null,
            IServiceScopeFactory? scopeFactory = null)
        {
            _core = new ChannelServerCore(options, configurePipeline, loggerFactory, scopeFactory);
        }

        /// <inheritdoc />
        public EndPoint? LocalEndPoint => _core.LocalEndPoint;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken ct) => _core.StartAsync(ct);

        /// <inheritdoc />
        public Task RunAsync(CancellationToken ct) => _core.RunAsync(ct);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken ct) => _core.StopAsync(ct);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _core.DisposeAsync();

        /// <inheritdoc />
        public void Dispose() => _core.Dispose();
    }
}
