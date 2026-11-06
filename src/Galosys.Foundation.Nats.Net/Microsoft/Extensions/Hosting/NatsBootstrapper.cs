using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Core;
using Galosys.Foundation.Nats.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// NATS 消费者启动器：负责扫描消费者标记、建立订阅，并在宿主停机时取消所有消费循环。
    /// </summary>
    public sealed class NatsBootstrapper : IHostedLifecycleService
    {
        private readonly IServiceProvider _root;
        private readonly ILogger<NatsBootstrapper> _logger;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<IConcurrentRunner> _runners = new();

        /// <summary>
        /// 初始化启动器。
        /// </summary>
        /// <param name="root">根服务提供者，用于扫描处理器并为每条消息创建作用域。</param>
        /// <param name="logger">日志记录器。</param>
        public NatsBootstrapper(IServiceProvider root, ILogger<NatsBootstrapper> logger)
        {
            _root = root;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            var connection = _root.GetService<INatsConnection>();
            if (connection is null)
            {
                throw new InvalidOperationException(
                    "未注册 NATS 客户端（INatsConnection）。请先调用 services.AddNatsClient(...)。");
            }

            var subscriptions = _root.RegisterHandlers();
            if (subscriptions.Count == 0)
            {
                _logger.LogInformation("未发现带 [NatsConsumer] 标记的处理器。");
                return Task.CompletedTask;
            }

            // IObjectSerializer 由 AddCore() 内的 AddObjectSerializer() 注册（JSON 实现）。
            // 若宿主未注册 core 服务，GetRequiredService 会抛异常 —— 这是明确的失败，
            // 好过静默用错序列化器。

            var serializer = _root.GetRequiredService<IObjectSerializer>();
            var dispatcher = new NatsConsumerDispatcher(_root, serializer, _logger);

            foreach (var subscription in subscriptions)
            {
                StartSubscription(connection, subscription, dispatcher);
            }

            _logger.LogInformation(
                "NATS 消费者启动完成，共 {Count} 个订阅。", subscriptions.Count);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            if (!_stopping.IsCancellationRequested)
                _stopping.Cancel();

            foreach (var runner in _runners)
            {
                await runner.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            _runners.Clear();
        }

        /// <inheritdoc />
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => StoppingAsync(cancellationToken);

        private void StartSubscription(
            INatsConnection connection,
            NatsSubscription subscription,
            NatsConsumerDispatcher dispatcher)
        {
            // Concurrency = N 时建立 N 个独立 runner（每个一个 worker），各自订阅同一 subject 与
            // queue group。不能用单个 N worker 的 runner：RunnerSlot 会反复调用 task，而
            // await foreach 只在取消时结束，单 runner 只会真正消费第一个 worker。
            for (var i = 0; i < subscription.Concurrency; i++)
            {
                var runner = ConcurrentRunner.CreateTaskRunner(1);
                _runners.Add(runner);

                runner.Start(ct => ConsumeAsync(connection, subscription, dispatcher, ct));
            }

            _logger.LogInformation(
                "NATS 订阅已建立: subject={Subject}, queueGroup={QueueGroup}, concurrency={Concurrency}",
                subscription.Subject,
                subscription.QueueGroup,
                subscription.Concurrency);
        }

        private async Task ConsumeAsync(
            INatsConnection connection,
            NatsSubscription subscription,
            NatsConsumerDispatcher dispatcher,
            CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, ct);

            await foreach (var msg in connection.SubscribeAsync<byte[]>(
                subscription.Subject,
                queueGroup: subscription.QueueGroup,
                cancellationToken: linked.Token).ConfigureAwait(false))
            {
                var payload = msg.Data;
                if (payload is null)
                    continue;

                try
                {
                    await dispatcher.DispatchWithRetryAsync(subscription, payload).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 派发器在处理器类型无法解析时会 rethrow（保留类型信息），
                    // 此处吸收以保证单条消息的失败不会终止整个订阅。
                    _logger.LogError(
                        ex,
                        "NATS 消息派发失败: subject={Subject}",
                        subscription.Subject);
                }
            }
        }
    }
}
