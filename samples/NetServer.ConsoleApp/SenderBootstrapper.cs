using Galosys.Foundation.Channel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace NetServer.ConsoleApp
{
    /// <summary>
    /// 后台客户端：使用框架 <see cref="ChannelClient"/> 周期性地发送请求并打印响应。
    /// </summary>
    /// <remarks>
    /// 客户端配置了重连与指数退避：服务端断开后会自动重连，退避间隔按
    /// 1 秒 → 2 秒 → 4 秒 递增（带抖动）。重连成功后会产生新的
    /// <see cref="ChannelContext"/>，因此需要为新连接重新创建发送器与会话。
    /// </remarks>
    public class SenderBootstrapper : BackgroundService
    {
        private const int Port = 8000;

        private readonly IChannelClient _client;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SenderBootstrapper> _logger;

        public SenderBootstrapper(
            ILoggerFactory loggerFactory,
            ILogger<SenderBootstrapper> logger)
        {
            _loggerFactory = loggerFactory;
            _logger = logger;

            _client = ChannelClientBuilder
                .Create(o =>
                {
                    o.EndPoint = new IPEndPoint(IPAddress.Loopback, Port);
                    o.ConnectTimeout = TimeSpan.FromSeconds(5);
                    o.Reconnect = new ChannelReconnectPolicy
                    {
                        // 0 表示无限重试；这里给一个有限值，避免服务端长时间不可用时示例永不退出。
                        MaxAttempts = 5,
                        InitialDelay = TimeSpan.FromSeconds(1),
                        MaxDelay = TimeSpan.FromSeconds(30),
                        BackoffMultiplier = 2.0,
                        JitterFactor = 0.2,
                    };
                })
                .Build();

            _client.StateChanged += OnStateChanged;
        }

        /// <summary>状态迁移时打印日志，便于观察重连过程。</summary>
        private void OnStateChanged(object? sender, ChannelStateChangedEventArgs e)
        {
            var failure = e.Failure is null ? string.Empty : $"（{e.Failure.Exception.Message}）";
            _logger.LogInformation("客户端状态: {Old} -> {New}{Failure}", e.OldState, e.NewState, failure);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _client.ConnectAsync(stoppingToken);

            var round = 0;

            // 一个连接只能创建一个会话；重连会产生新的连接，此时要换一个新的发送器。
            ChannelSender? sender = null;
            ChannelContext? senderConnection = null;

            while (!stoppingToken.IsCancellationRequested)
            {
                var connection = _client.Connection;
                if (connection is null)
                {
                    // 连接断开且正在重连；退避由 ChannelClient 内部处理，这里只等待。
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                // 连接换了（首连或重连）就重建发送器：会话绑定在具体连接上，不能跨连接复用。
                if (sender is null || !ReferenceEquals(senderConnection, connection))
                {
                    sender = new ChannelSender(connection, _loggerFactory.CreateLogger<ChannelSender>());
                    senderConnection = connection;
                }

                try
                {
                    // 前几轮用批量写演示一次 flush 写多条；之后退化为单条请求。
                    if (round < 3)
                    {
                        await sender.SendBatchAndReceiveAsync(
                            new[] { $"batch-{round}-a", $"batch-{round}-b" },
                            stoppingToken);
                    }
                    else
                    {
                        await sender.SendAndReceiveAsync($"hello-{round}", stoppingToken);
                    }
                }
                catch (EndOfStreamException)
                {
                    // 对端关闭了连接；等待重连后继续。
                    _logger.LogWarning("连接已被对端关闭，等待重连。");
                }
                catch (IOException)
                {
                    // 连接已断开，写入被拒绝；等待重连后继续。
                    _logger.LogWarning("连接已断开，等待重连。");
                }
                catch (InvalidOperationException)
                {
                    // 旧连接的 PipeWriter 已完成，写入被拒绝；等待重连后换新连接。
                    _logger.LogWarning("连接已失效，等待重连。");
                }

                round++;
                await Task.Delay(1000, stoppingToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);

            _client.StateChanged -= OnStateChanged;
            await _client.DisposeAsync();
        }
    }
}
