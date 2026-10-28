using DurableTask.Core;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// 
    /// </summary>
    public static class DurableTaskServiceCollectionExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddDurableTaskCore(this IServiceCollection services)
        {
            var types = DependencyContext.Default.GetImplementTypes();
            var orchestrationTypes = types.Where(i => i.IsAssignableToGeneric(typeof(TaskOrchestration<,>)));
            var activityTypes = types.Where(i => i.IsAssignableToGeneric(typeof(TaskActivity<,>)));

            foreach (var orchestrationType in orchestrationTypes)
                services.AddTransient(orchestrationType);

            foreach (var activityType in activityTypes)
                services.AddTransient(activityType);

            services.AddSingleton(sp =>
            {
                var svc = sp.GetRequiredService<IOrchestrationService>();
                var worker = new TaskHubWorker(svc, loggerFactory: sp.GetRequiredService<ILoggerFactory>());

                if (orchestrationTypes.Any())
                {
                    var orchestrationCreators = orchestrationTypes.Select(i => new ServiceProviderObjectCreator<TaskOrchestration>(sp, i)).ToArray();
                    worker.AddTaskOrchestrations(orchestrationCreators);

                    if (activityTypes.Any())
                    {
                        var activityCreators = activityTypes.Select(i => new ServiceProviderObjectCreator<TaskActivity>(sp, i)).ToArray();
                        worker.AddTaskActivities(activityCreators);
                    }
                }

                return worker;
            });

            services.AddSingleton(sp =>
            {
                var svc = sp.GetRequiredService<IOrchestrationServiceClient>();
                return new TaskHubClient(svc, loggerFactory: sp.GetRequiredService<ILoggerFactory>());
            });
            services.AddHostedService<DurableTaskHostedService>();

            return services;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="services"></param>
        /// <param name="implementationFactory"></param>
        /// <returns></returns>
        public static IServiceCollection AddDurableTaskOrchestration<T>(this IServiceCollection services, Func<IServiceProvider, T> implementationFactory)
            where T : class, IOrchestrationService, IOrchestrationServiceClient
        {
            services.AddSingleton(implementationFactory);
            services.AddSingleton<IOrchestrationService>(sp => sp.GetRequiredService<T>());
            services.AddSingleton<IOrchestrationServiceClient>(sp => sp.GetRequiredService<T>());

            return services;
        }
    }
}
