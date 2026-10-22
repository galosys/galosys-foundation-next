using Microsoft.Extensions.Hosting.Net;
using System.Text;

namespace NetServer.ConsoleApp
{
    public sealed class SampleChannelServer : ChannelServerBase
    {
        private readonly ILogger<SampleChannelServer> _logger;

        public SampleChannelServer(ChannelServerOptions options, ILogger<SampleChannelServer> logger)
            : base(options)
        {
            _logger = logger;
        }

        protected override async Task ChannelHandleAsync(ChannelContext context)
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
