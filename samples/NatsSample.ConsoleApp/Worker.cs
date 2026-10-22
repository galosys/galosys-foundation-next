using NATS.Client.Core;

namespace NatsSample.ConsoleApp
{
    [HostedService]
    public class Worker(ILogger<Worker> logger, INatsConnection nats) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                //await Task.Delay(3000);
                await nats.PublishAsync("sample.greet", "hello");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
