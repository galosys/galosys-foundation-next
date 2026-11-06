using System.Text;
using Galosys.Foundation.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Messaging;
using NATS.Client.Core;

namespace Galosys.Foundation.Nats.Net
{
    /// <summary>
    /// NATS 消费者派发器：把收到的消息负载反序列化为处理器形参类型并调用处理器。
    /// </summary>
    internal sealed class NatsConsumerDispatcher(
        IServiceProvider root,
        IObjectSerializer serializer,
        ILogger logger)
    {
        private readonly IServiceProvider _root = root;
        private readonly IObjectSerializer _serializer = serializer;
        private readonly ILogger _logger = logger;

        /// <summary>
        /// 处理一条消息：反序列化、解析处理器实例、调用处理器方法。
        /// </summary>
        /// <param name="subscription">订阅描述。</param>
        /// <param name="payload">消息原始负载。</param>
        /// <returns>处理是否成功。</returns>
        public async Task<bool> DispatchAsync(NatsSubscription subscription, byte[] payload)
        {
            object? message;

            try
            {
                message = _serializer.Deserialize(
                    Encoding.UTF8.GetString(payload),
                    subscription.MessageType);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS 消息反序列化失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.MessageType.FullName);
                return false;
            }

            if (message is null)
            {
                _logger.LogWarning(
                    "NATS 消息反序列化结果为 null: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.MessageType.FullName);
                return false;
            }

            using var scope = _root.CreateScope();

            object handler;
            try
            {
                handler = scope.ServiceProvider.GetRequiredService(
                    subscription.HandlerMethod.DeclaringType
                    ?? throw new InvalidOperationException("消费者方法没有声明类型。"));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS 消费者类型解析失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.HandlerMethod.DeclaringType?.FullName);
                throw;
            }

            if (handler is not IMessageHandler messageHandler)
            {
                _logger.LogError(
                    "NATS 消费者未实现 IMessageHandler 契约: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.HandlerMethod.DeclaringType?.FullName);
                return false;
            }

            try
            {
                // IMessageHandler<T> 带有默认接口实现，会转发到 HandleAsync(T)；
                // 这里按非泛型契约调用，避免每条消息上的反射与实参数组分配。
                return await messageHandler.HandleAsync((IMessage)message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS 消费者处理失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.HandlerMethod.DeclaringType?.FullName);
                return false;
            }
        }

        /// <summary>
        /// 按配置的次数重试执行处理器。
        /// </summary>
        /// <param name="subscription">订阅描述。</param>
        /// <param name="payload">消息原始负载。</param>
        /// <returns>处理是否成功。</returns>
        public async Task<bool> DispatchWithRetryAsync(NatsSubscription subscription, byte[] payload)
        {
            var attempt = 0;

            while (true)
            {
                if (await DispatchAsync(subscription, payload).ConfigureAwait(false))
                    return true;

                if (attempt >= subscription.MaxRetryCount)
                    return false;

                var delay = subscription.RetryBaseDelayMs * (int)Math.Pow(2, attempt);
                attempt++;

                _logger.LogWarning(
                    "NATS 消费者重试: subject={Subject}, attempt={Attempt}, delayMs={Delay}",
                    subscription.Subject,
                    attempt,
                    delay);

                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }
}
