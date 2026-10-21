using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Messaging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Net;
using System.Collections.Concurrent;
using System.Reflection;

namespace System
{
    /// <summary>
    /// 服务提供者扩展，负责自动注册消息处理器 galoS@2026-04-15 10:00:00
    /// </summary>
    internal static class ServiceProviderExtensions
    {
        private static readonly ConcurrentDictionary<MethodInfo, DateTime> _handlersRegistered = new();

        /// <summary>
        /// 自动扫描并注册所有消息处理器
        /// </summary>
        /// <param name="root">根服务提供者</param>
        public static async Task RegisterAllHandlersAsync(this IServiceProvider root)
        {
            await root.RegisterHandlerAsync();
        }

        /// <summary>
        /// 注册指定类型的消息处理器
        /// </summary>
        /// <param name="root">根服务提供者</param>
        public static async Task RegisterHandlerAsync<T>(this IServiceProvider root)
        {
            await root.RegisterHandlerAsync(typeof(T));
        }

        /// <summary>
        /// 注册指定类型或自动扫描的消息处理器
        /// </summary>
        /// <param name="root">根服务提供者</param>
        /// <param name="handlerTypes">指定处理器类型</param>
        public static async Task RegisterHandlerAsync(this IServiceProvider root, params Type[] handlerTypes)
        {
            var classTypes = DependencyContext.Default.GetImplementTypes();

            if (handlerTypes != null && handlerTypes.Any())
                classTypes = classTypes.Where(type => handlerTypes.Contains(type));

            if (!classTypes.Any())
                return;

            var mis = classTypes.SelectMany(i => i.GetMethods())
                .Where(m => m.GetCustomAttributes<NatsConsumerAttribute>(false).Any());

            mis = mis.Where(i => !_handlersRegistered.ContainsKey(i));

            if (!mis.Any())
                return;

            using (var scope = root.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var template = sp.GetRequiredService<INatsConnection>();
                var options = sp.GetRequiredService<IOptionsMonitor<NatsOptions>>().CurrentValue;

                foreach (var mi in mis)
                {
                    if (_handlersRegistered.TryGetValue(mi, out _))
                        continue;

                    var handlers = mi.GetCustomAttributes<NatsConsumerAttribute>(false);

                    foreach (var handler in handlers)
                    {
                        var paras = mi.GetParameters();
                        var first = paras[0];
                        var firstType = first.ParameterType;

                        try
                        {
                            ConcurrentRunner.CreateTaskRunner(1).Start(async ct =>
                            {
                                await foreach (var msg in template.SubscribeAsync<string>("sample.greet", queueGroup: "nats-sample-console-app", cancellationToken: cts.Token))
                                {
                                    using (var innerScope = root.CreateScope())
                                    {
                                        var innerSp = innerScope.ServiceProvider;
                                        var instance = innerSp.GetRequiredService(mi.DeclaringType) as IMessageHandler;
                                        var rt = await instance.HandleAsync(msg.Data);
                                        return rt;
                                    }
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            var logger = root.GetService<ILogger<INatsConnection>>();
                            //logger?.LogError(ex, "注册 AMQP Handler 失败: routingKey={RoutingKey}", handler.RoutingKey);
                            throw;
                        }

                        _handlersRegistered.TryAdd(mi, DateTime.UtcNow);
                    }
                }
            }
        }
    }
}
