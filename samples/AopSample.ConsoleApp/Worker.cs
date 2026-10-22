using AopSample.ConsoleApp.Application;

namespace AopSample.ConsoleApp
{
    [HostedService]
    public class Worker(ILogger<Worker> logger, IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var usrSvc = sp.GetRequiredService<UserService>();
                await usrSvc.SayHelloAsync();
            }
        }
    }
}
