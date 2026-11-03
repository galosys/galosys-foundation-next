using Galosys.Foundation.Channel;
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
            // session 只在终端 handler 中创建；一个连接只能有一个 session。
            // 读和写复用同一个 session：读侧独占，写侧由 session 内部信号量串行化。
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

                // 回包，形成 request/response 往返。
                await session.WriteAsync($"echo: {line}", context.ConnectionClosed);
            }
        }
    }
}
