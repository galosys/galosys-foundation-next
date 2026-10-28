using DurableTask.Core;
using DurableTask.Emulator;

namespace DTFx.ConsoleApp
{
    public class Worker(ILogger<Worker> logger) : IHostedService
    {
        TaskHubWorker _worker;
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            //var connectionString = "";
            //var svc = new SqlOrchestrationService(new SqlOrchestrationServiceSettings(connectionString));
            //await svc.CreateIfNotExistsAsync();

            var svc = new LocalOrchestrationService();

            _worker = new TaskHubWorker(svc);
            _worker.AddTaskOrchestrations(typeof(SampleOrchestration));
            _worker.AddTaskActivities(typeof(SayHelloActivity));
            await _worker.StartAsync();

            var client = new TaskHubClient(svc);
            await client.CreateOrchestrationInstanceAsync(typeof(SampleOrchestration), "Hello");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _worker.StopAsync();
        }
    }

    class SampleOrchestration : TaskOrchestration<string, string>
    {
        public override Task<string> RunTask(OrchestrationContext context, string input)
        {
            return context.ScheduleTask<string>(typeof(SayHelloActivity), input);
        }
    }

    class SayHelloActivity : TaskActivity<string, string>
    {
        protected override string Execute(TaskContext context, string input)
        {
            return "ok";
        }
    }
}
