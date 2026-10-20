using MafSample.ConsoleApp.Application;

namespace MafSample.ConsoleApp
{
    class InternalModule : IModule
    {
        public void ConfigureServices(IServiceCollection services, HostBuilderContext context)
        {
            services.AddSingleton<Worker>();
        }
    }
}
