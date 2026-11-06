using Galosys.Foundation.Nats.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// NATS 消费者服务注册扩展方法。
    /// </summary>
    public static class NatsServiceCollectionExtensions
    {
        /// <summary>
        /// 绑定 NATS 消费者选项并注册消费者生命周期。
        /// </summary>
        /// <param name="services">服务集合。</param>
        /// <param name="configuration">配置源；消费者选项绑定到 <c>Nats</c> 配置节。</param>
        /// <returns>同一个服务集合，便于链式调用。</returns>
        public static IServiceCollection AddNats(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddOptions<NatsOptions>()
                .Bind(configuration.GetSection("Nats"));
            services.AddNats();
            return services;
        }

        /// <summary>
        /// 注册消费者生命周期，不绑定配置。
        /// </summary>
        /// <param name="services">服务集合。</param>
        /// <returns>同一个服务集合，便于链式调用。</returns>
        public static IServiceCollection AddNats(this IServiceCollection services)
        {
            services.AddOptions<NatsOptions>();
            services.AddHostedService<NatsBootstrapper>();
            return services;
        }
    }
}
