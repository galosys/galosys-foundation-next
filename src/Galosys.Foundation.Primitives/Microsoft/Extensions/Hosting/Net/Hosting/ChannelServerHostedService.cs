using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把服务器生命周期映射到通用宿主。</summary>
    internal sealed class ChannelServerHostedService<TChannelServer> : IHostedService
        where TChannelServer : ChannelServerBase
    {
        private readonly TChannelServer _server;

        public ChannelServerHostedService(
            TChannelServer server,
            IServiceScopeFactory scopeFactory,
            ILoggerFactory loggerFactory)
        {
            _server = server;
            _server.ScopeFactory = scopeFactory;
            _server.LoggerFactory = loggerFactory;
        }

        /// <summary>宿主启动时绑定并开始接受连接。</summary>
        public Task StartAsync(CancellationToken cancellationToken) => _server.StartAsync(cancellationToken);

        /// <summary>宿主停机时停止接受连接并等待在途连接。</summary>
        public Task StopAsync(CancellationToken cancellationToken) => _server.StopAsync(CancellationToken.None);
    }
}
