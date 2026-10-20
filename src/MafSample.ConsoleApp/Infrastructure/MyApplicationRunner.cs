namespace MafSample.ConsoleApp.Infrastructure
{
    [Service]
    internal class MyApplicationRunner : IApplicationRunner
    {
        public Task RunAsync(ApplicationArguments args, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
