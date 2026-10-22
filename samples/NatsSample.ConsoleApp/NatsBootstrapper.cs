using NATS.Client.Core;
using NATS.Net;

namespace NatsSample.ConsoleApp
{
    [HostedService]
    public class NatsBootstrapper : BackgroundService
    {
        private readonly IConcurrentRunner _runner;
        private readonly ILogger<Worker> _logger;
        private readonly INatsConnection _nats;

        public NatsBootstrapper(ILogger<Worker> logger, INatsConnection nats)
        {
            _runner = ConcurrentRunner.CreateTaskRunner(2);
            _logger = logger;
            _nats = nats;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var js = _nats.CreateJetStreamContext();

            _runner.Start(async ct =>
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, ct);
                await foreach (var msg in _nats.SubscribeAsync<string>("sample.greet", queueGroup: "nats-sample-console-app", cancellationToken: cts.Token))
                {
                    await Console.Out.WriteLineAsync($">>>>> {msg.Data} <<<<<");
                }
            });
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await _runner.StopAsync();
        }
    }
}
