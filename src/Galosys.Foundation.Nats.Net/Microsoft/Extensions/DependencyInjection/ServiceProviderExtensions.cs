using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Galosys.Foundation.Core;
using Galosys.Foundation.Nats.Net;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Messaging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Net;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// 服务提供者扩展，负责扫描并注册 NATS 消费者。
    /// </summary>
    internal static class ServiceProviderExtensions
    {
        private static readonly ConcurrentDictionary<(MethodInfo Method, string Subject, string? QueueGroup), DateTime>
            _handlersRegistered = new();

        /// <summary>
        /// 扫描并注册所有被 <see cref="NatsConsumerAttribute"/> 标记的处理器，返回待建立的订阅描述。
        /// </summary>
        /// <param name="root">根服务提供者。</param>
        /// <param name="handlerTypes">限定扫描的类型；为空表示扫描全部。</param>
        /// <returns>待建立的订阅描述集合。</returns>
        internal static IReadOnlyList<NatsSubscription> RegisterHandlers(
            this IServiceProvider root,
            params Type[] handlerTypes)
        {
            var context = DependencyContext.Default;
            if (context is null)
            {
                root.GetService<ILogger<INatsConnection>>()
                    ?.LogWarning("DependencyContext 不可用，跳过 NATS 消费者扫描。");
                return Array.Empty<NatsSubscription>();
            }

            var candidates = context.GetImplementTypes();
            if (handlerTypes is { Length: > 0 })
                candidates = candidates.Where(t => handlerTypes.Contains(t));

            var marked = candidates
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Select(m => (Method: m, Attributes: m.GetCustomAttributes<NatsConsumerAttribute>(false).ToArray())))
                .Where(x => x.Attributes.Length > 0)
                .ToArray();

            if (marked.Length == 0)
                return Array.Empty<NatsSubscription>();

            var options = root.GetRequiredService<IOptionsMonitor<NatsOptions>>().CurrentValue;
            var logger = root.GetService<ILogger<NatsBootstrapper>>()
                ?? (ILogger?)root.GetService<ILogger<INatsConnection>>();

            var subscriptions = new List<NatsSubscription>();

            foreach (var (method, attributes) in marked)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    logger?.LogWarning(
                        "NATS 消费者方法 {Method} 没有形参，跳过。", method.DeclaringType?.FullName);
                    continue;
                }

                var messageType = parameters[0].ParameterType;

                foreach (var attribute in attributes)
                {
                    if (string.IsNullOrWhiteSpace(attribute.Subject))
                    {
                        logger?.LogWarning(
                            "NATS 消费者方法 {Method} 的 subject 为空，跳过。", method.DeclaringType?.FullName);
                        continue;
                    }

                    var key = (method, attribute.Subject, attribute.QueueGroup);
                    if (_handlersRegistered.ContainsKey(key))
                        continue;

                    if (!IsMessageType(messageType, method, logger))
                        continue;

                    var property = ResolveHandlerProperty(options, attribute.Subject);

                    subscriptions.Add(new NatsSubscription(
                        attribute.Subject,
                        attribute.QueueGroup,
                        method,
                        messageType,
                        property.Concurrency,
                        property.MaxRetryCount,
                        property.RetryBaseDelayMs));

                    _handlersRegistered.TryAdd(key, DateTime.UtcNow);
                }
            }

            return subscriptions;
        }

        private static bool IsMessageType(Type messageType, MethodInfo method, ILogger? logger)
        {
            if (typeof(IMessage).IsAssignableFrom(messageType))
                return true;

            logger?.LogWarning(
                "NATS 消费者方法 {Method} 的形参类型 {MessageType} 未实现 IMessage，跳过。",
                method.DeclaringType?.FullName,
                messageType.FullName);

            return false;
        }

        private static (int Concurrency, int MaxRetryCount, int RetryBaseDelayMs) ResolveHandlerProperty(
            NatsOptions options,
            string subject)
        {
            var concurrency = options.Concurrency;
            var maxRetry = options.MaxRetryCount;
            var retryDelay = options.RetryBaseDelayMs;

            if (options.Handlers.TryGetValue(subject, out var property) && property is not null)
            {
                concurrency = property.Concurrency ?? concurrency;
                maxRetry = property.MaxRetryCount ?? maxRetry;
                retryDelay = property.RetryBaseDelayMs ?? retryDelay;
            }

            return (Math.Max(concurrency, 1), Math.Max(maxRetry, 0), Math.Max(retryDelay, 0));
        }
    }
}
