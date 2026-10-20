using Microsoft.Extensions.Hosting.Net;
using System.Net;
using System.Text;

namespace NetServer.ConsoleApp
{
    public class ListenerBootstrapper : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public ListenerBootstrapper(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var srv = sp.GetRequiredService<SampleChannelServer>();
                await srv.RunAsync(stoppingToken);
            }
        }
    }

    class SampleChannelServer : ChannelServerBase
    {
        private readonly ILogger<SampleChannelServer> _logger;

        public SampleChannelServer(EndPoint endPoint, ILogger<SampleChannelServer> logger) : base(endPoint)
        {
            _logger = logger;
        }

        protected override async Task ChannelHandleAsync(ChannelContext context)
        {
            var reader = context.Reader;

            while (true)
            {
                var rt = await reader.ReadAsync();
                var buffer = rt.Buffer;

                if (!buffer.IsEmpty)
                {
                    var str = Encoding.UTF8.GetString(buffer);
                    _logger.LogInformation(">>>>> {str} <<<<<", str);
                }

                reader.AdvanceTo(buffer.End);

                if (rt.IsCompleted)
                    break;
            }
        }
    }
}

