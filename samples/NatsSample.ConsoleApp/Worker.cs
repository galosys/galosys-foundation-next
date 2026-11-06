using NATS.Client.Core;
using NatsSample.ConsoleApp.Adapter.Handlers;

namespace NatsSample.ConsoleApp
{
    /// <summary>
    /// 示例生产者：等待消费者订阅建立后，发布一条类型化的示例消息。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="nats">NATS 连接。</param>
    [HostedService]
    public class Worker(ILogger<Worker> logger, INatsConnection nats) : BackgroundService
    {
        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // 等待消费者订阅建立，避免消息在订阅前发布而丢失。
                await Task.Delay(3000, stoppingToken);

                await nats.PublishAsync(
                    "sample.greet",
                    new SampleGreatEvent { Text = "hello" });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常停机。
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发布 NATS 消息失败。");
            }
        }
    }
}
