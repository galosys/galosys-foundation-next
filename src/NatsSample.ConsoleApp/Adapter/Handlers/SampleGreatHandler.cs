using Microsoft.Extensions.Messaging;

namespace NatsSample.ConsoleApp.Adapter.Handlers
{
    [Handler]
    internal class SampleGreatHandler
        : IMessageHandler<SampleGreatEvent>
    {
        //[NatsConsumer("sample.greate",queueGroup: "nats-sample-console-app")]
        public Task<bool> HandleAsync(SampleGreatEvent msg)
        {
            throw new NotImplementedException();
        }
    }

    internal class SampleGreatEvent : MessageBase
    {

    }
}
