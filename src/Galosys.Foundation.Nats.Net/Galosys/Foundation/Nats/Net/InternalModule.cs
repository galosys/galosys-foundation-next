using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Galosys.Foundation.Nats.Net
{
    /// <summary>
    /// NATS 消费者模块。启用模块化启动的宿主无需手工调用 <c>AddNats</c> 即可获得消费者功能。
    /// </summary>
    internal sealed class InternalModule : IModule
    {
        /// <inheritdoc />
        public void ConfigureServices(IServiceCollection services, HostBuilderContext context)
        {
            services.AddNats(context.Configuration);
        }
    }
}
