using System.Text;
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
            var reader = context.Reader;

            while (true)
            {
                var rt = await reader.ReadAsync();
                var buffer = rt.Buffer;

                if (!buffer.IsEmpty)
                {
                    var str = Encoding.UTF8.GetString(buffer);
                    _logger.LogInformation(">>>>> {str} <<<<<", str);
                }

                reader.AdvanceTo(buffer.End);

                if (rt.IsCompleted || rt.IsCanceled)
                    break;
            }
        }
    }
}
