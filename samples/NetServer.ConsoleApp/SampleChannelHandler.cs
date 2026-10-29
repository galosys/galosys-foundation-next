using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;

namespace NetServer.ConsoleApp
{
    public sealed class SampleChannelHandler
    {
        private readonly ILogger<SampleChannelHandler> _logger;

        public SampleChannelHandler(ILogger<SampleChannelHandler> logger)
        {
            _logger = logger;
        }

        public async Task HandleAsync(ChannelContext context)
        {
            await using var session = context.AsLineTextSession();

            while (true)
            {
                string line;
                try
                {
                    line = await session.ReadAsync(context.ConnectionClosed);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (OperationCanceledException) when (context.ConnectionClosed.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogInformation(">>>>> {Line} <<<<<", line);
            }
        }
    }
}
