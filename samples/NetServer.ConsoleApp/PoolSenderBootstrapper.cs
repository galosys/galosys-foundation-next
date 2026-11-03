using Galosys.Foundation.Channel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetServer.ConsoleApp
{
    /// <summary>
    /// 连接池示例：从 <see cref="ChannelClientPool"/> 租借一个客户端、发一条消息、读回响应后归还。
    /// </summary>
    /// <remarks>
    /// 归还即失去所有权：调用 <see cref="ChannelClientPool.ReturnAsync"/> 之后不得再访问该客户端。
    /// 池模式不配置重连策略——池自己做健康检查与断线替换，客户端自带重连会与之竞争。
    /// <para>
    /// 池会复用同一个连接，因此同一个客户端只能创建一次会话；
    /// 这里按客户端缓存发送器，避免重复创建会话。
    /// </para>
    /// </remarks>
    public class PoolSenderBootstrapper : BackgroundService
    {
        private const int Port = 8000;

        private readonly ChannelClientPool _pool;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<PoolSenderBootstrapper> _logger;

        // 池会复用连接，同一个客户端只能有一个会话；按客户端缓存发送器。
        private readonly Dictionary<IChannelClient, ChannelSender> _senders = new();
        private readonly Dictionary<IChannelClient, ChannelContext> _connections = new();

        public PoolSenderBootstrapper(
            ChannelClientPool pool,
            ILoggerFactory loggerFactory,
            ILogger<PoolSenderBootstrapper> logger)
        {
            _pool = pool;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var round = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                // 租借一个已连接的客户端；池满时会等待归还，最多等 RentTimeout。
                var client = await _pool.RentAsync(stoppingToken);

                try
                {
                    var connection = client.Connection;
                    if (connection is null)
                    {
                        _logger.LogWarning("租借到的客户端没有可用连接。");
                        continue;
                    }

                    if (!_senders.TryGetValue(client, out var sender) || !ReferenceEquals(_connections[client], connection))
                    {
                        // 连接换了就重建发送器：会话绑定在具体连接上，不能跨连接复用。
                        sender = new ChannelSender(connection, _loggerFactory.CreateLogger<ChannelSender>());
                        _senders[client] = sender;
                        _connections[client] = connection;
                    }

                    await sender.SendAndReceiveAsync($"pool-hello-{round}", stoppingToken);
                }
                catch (EndOfStreamException)
                {
                    _logger.LogWarning("连接已被对端关闭。");
                }
                catch (IOException)
                {
                    _logger.LogWarning("连接已断开。");
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning("连接已失效。");
                }
                finally
                {
                    // 归还即转移所有权：归还后不能再使用这个客户端。
                    await _pool.ReturnAsync(client);
                }

                round++;
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
