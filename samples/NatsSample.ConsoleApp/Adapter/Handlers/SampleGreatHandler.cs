using Microsoft.Extensions.Messaging;
using NATS.Net;

namespace NatsSample.ConsoleApp.Adapter.Handlers
{
    [Handler]
    internal class SampleGreatHandler
        : IMessageHandler<SampleGreatEvent>
    {
        private readonly ILogger<SampleGreatHandler> _logger;

        public SampleGreatHandler(ILogger<SampleGreatHandler> logger)
        {
            _logger = logger;
        }

        [NatsConsumer("sample.greet", queueGroup: "nats-sample-console-app")]
        public Task<bool> HandleAsync(SampleGreatEvent msg)
        {
            _logger.LogInformation("收到 NATS 消息: {@msg}", msg);
            return Task.FromResult(true);
        }
    }

    internal class SampleGreatEvent : MessageBase
    {
        public string? Text { get; set; }
    }
}
