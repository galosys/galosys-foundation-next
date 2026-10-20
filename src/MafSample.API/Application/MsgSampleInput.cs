//using Galosys.Foundation.RocketMQ.Abstractions;
using Microsoft.Extensions.Messaging;
using RabbitMQ.Client;

namespace MafSample.API.Application
{
    [Handler]
    internal class MsgSampleInputHandler : IMessageHandler<MsgSampleInput>
    {
        private readonly ILogger<MsgSampleInputHandler> _logger;

        public MsgSampleInputHandler(
            ILogger<MsgSampleInputHandler> logger)
        {
            _logger = logger;
        }

        //[RmqHandler("test_send")]
        [AmqpHandler("test_send")]
        public async Task<bool> HandleAsync(MsgSampleInput msg)
        {
            _logger.LogInformation("{@msg}",msg);
            //await Task.Delay(10);
            return true;
        }
    }
    public class MsgSampleInput : MessageBase
    {

    }
}
