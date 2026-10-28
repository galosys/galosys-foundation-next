using DurableTask.Core;

namespace DTFx.ConsoleApp
{
    public class Worker(ILogger<Worker> logger, IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var client = sp.GetRequiredService<TaskHubClient>();
                var instance = await client.CreateOrchestrationInstanceAsync(typeof(SampleOrchestration), "user-1");
            }
        }
    }

    class SampleOrchestration : TaskOrchestration<string, string>
    {
        private readonly ILogger<SampleOrchestration> _logger;

        public SampleOrchestration(ILogger<SampleOrchestration> logger)
        {
            _logger = logger;
        }

        public override Task<string> RunTask(OrchestrationContext context, string input)
        {
            if (context.IsReplaying)
                _logger.LogInformation(">>>>> Orchestration replaying: {input} <<<<<", input);
            else
                _logger.LogInformation(">>>>> Orchestration: {input} <<<<<", input);

            return context.ScheduleTask<string>(typeof(SayHelloActivity), input);
        }
    }

    class SayHelloActivity : TaskActivity<string, string>
    {
        private readonly ILogger<SayHelloActivity> _logger;

        public SayHelloActivity(ILogger<SayHelloActivity> logger)
        {
            _logger = logger;
        }

        protected override string Execute(TaskContext context, string input)
        {
            _logger.LogInformation(">>>>> Activity: {input} <<<<<", input);
            return "ok";
        }
    }
}
