using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>服务器 DI 注册扩展。</summary>
    public static class ChannelServerServiceCollectionExtensions
    {
        /// <summary>
        /// 注册服务器 Options、单例与 hosted service。
        /// <typeparamref name="TChannelServer"/> 的构造函数必须可解析
        /// <see cref="ChannelServerOptions"/>（例如 <c>(ChannelServerOptions, ILogger&lt;T&gt;)</c>）。
        /// </summary>
        public static IServiceCollection AddChannelServer<TChannelServer>(
            this IServiceCollection services,
            Action<ChannelServerOptions>? configure = null)
            where TChannelServer : ChannelServerBase
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton(_ =>
            {
                var options = new ChannelServerOptions();
                configure?.Invoke(options);
                options.Validate();
                return options;
            });

            services.AddSingleton<TChannelServer>();
            services.AddSingleton<IChannelServer>(sp => sp.GetRequiredService<TChannelServer>());
            services.AddHostedService<ChannelServerHostedService<TChannelServer>>();

            return services;
        }
    }
}
