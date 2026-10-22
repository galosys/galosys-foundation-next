using System.Threading.Channels;

namespace MafSample.ConsoleApp.Application
{
    [HostedService]
    public class Worker : WindowedBatchWorker<SampleInput, string>
    {
        public Worker(IServiceScopeFactory scopeFactory) : base(scopeFactory)
        {
        }

        protected override string GetGroupKey(SampleInput message)
        {
            return message.Itemcode;
        }

        protected override Task ProcessBatchAsync(string groupKey, IReadOnlyList<SampleInput> batch, IServiceProvider sp, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await EnqueueAsync(new SampleInput("12"));
            await base.ExecuteAsync(stoppingToken);
        }
    }
}
