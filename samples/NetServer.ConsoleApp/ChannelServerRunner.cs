using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NetServer.ConsoleApp
{
    /// <summary>把 ChannelServer 绑定到宿主启动/停机。</summary>
    public sealed class ChannelServerRunner : IHostedService
    {
        private readonly ChannelServer _server;

        public ChannelServerRunner(ChannelServer server) => _server = server;

        public Task StartAsync(CancellationToken cancellationToken)
            => _server.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken)
            => _server.StopAsync(CancellationToken.None);
    }
}
