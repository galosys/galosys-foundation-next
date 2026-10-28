using DurableTask.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting
{
    internal class DurableTaskHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        TaskHubWorker _worker;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="serviceProvider"></param>
        public DurableTaskHostedService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _worker = _serviceProvider.GetRequiredService<TaskHubWorker>();
            await _worker.StartAsync();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _worker.StopAsync();
        }
    }
}
