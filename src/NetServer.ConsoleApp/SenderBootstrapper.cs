using System.Net.Sockets;
using System.Text;

namespace NetServer.ConsoleApp
{
    public class SenderBootstrapper(ILogger<ListenerBootstrapper> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, protocolType: ProtocolType.Tcp);
            await client.ConnectAsync("127.0.0.1", 8000);

            var bytes = Encoding.UTF8.GetBytes("hello");

            while (!stoppingToken.IsCancellationRequested)
            {
                await client.SendAsync(bytes);
                await Task.Delay(1000);
            }
        }
    }
}
