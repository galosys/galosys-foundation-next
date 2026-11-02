using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>多服务器宿主注册扩展。</summary>
    public static class ChannelServerGroupServiceCollectionExtensions
    {
        /// <summary>追加一个服务器定义；注册顺序即启动顺序。</summary>
        /// <param name="services">服务集合。</param>
        /// <param name="definition">服务器定义。</param>
        /// <returns>同一个 <paramref name="services"/>，便于链式调用。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> 或 <paramref name="definition"/> 为 null。</exception>
        /// <remarks>只追加定义，不注册全局 <c>ChannelServerOptions</c>；没有组注册时定义不会监听。</remarks>
        public static IServiceCollection AddChannelServer(
            this IServiceCollection services,
            ChannelServerDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(definition);

            services.AddSingleton(definition);
            return services;
        }

        /// <summary>注册一个由所有定义构建的服务器组及其 hosted service。</summary>
        /// <param name="services">服务集合。</param>
        /// <returns>同一个 <paramref name="services"/>，便于链式调用。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 null。</exception>
        /// <remarks>
        /// 重复调用幂等：组只注册一次，hosted service 按实现类型去重。
        /// 组在解析时读取全部定义，并用容器的 <see cref="ILoggerFactory"/> 与
        /// <see cref="IServiceScopeFactory"/> 创建每个 server。
        /// </remarks>
        public static IServiceCollection AddChannelServerGroup(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton(sp =>
            {
                var definitions = sp.GetServices<ChannelServerDefinition>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

                var servers = definitions
                    .Select(definition => new ChannelServer(
                        definition.Options,
                        definition.ConfigurePipeline,
                        loggerFactory,
                        scopeFactory))
                    .ToArray();

                return new ChannelServerGroup(servers);
            });

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, ChannelServerGroupHostedService>());

            return services;
        }
    }
}
