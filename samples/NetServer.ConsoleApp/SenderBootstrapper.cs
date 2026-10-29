using System.Net.Sockets;
using System.Text;

namespace NetServer.ConsoleApp
{
    public class SenderBootstrapper : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync("127.0.0.1", 8000, stoppingToken);

            var bytes = Encoding.UTF8.GetBytes("hello\n");

            while (!stoppingToken.IsCancellationRequested)
            {
                await client.SendAsync(bytes, stoppingToken);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
