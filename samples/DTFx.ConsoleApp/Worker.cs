using DurableTask.Core;
using DurableTask.SqlServer;
using Newtonsoft.Json;

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

                var svc = sp.GetRequiredService<SqlOrchestrationService>();
                await svc.CreateIfNotExistsAsync();

                var instanceId = "user-1";

                var state = await client.GetOrchestrationStateAsync(instanceId);
                var instance = await client.CreateOrchestrationInstanceAsync(typeof(SampleOrchestration), instanceId, "user-1");
                await client.RaiseAsync(instance, "Approval", new ApprovalResult
                {
                    BusinessId = 1,
                    BusinessType = 1,
                    ApproverId = "user-1"
                });
            }
        }
    }

    class SampleOrchestration : TaskOrchestration<string, string>
    {
        private TaskCompletionSource<ApprovalResult> _approvalSource = new TaskCompletionSource<ApprovalResult>();
        private readonly ILogger<SampleOrchestration> _logger;

        public SampleOrchestration(ILogger<SampleOrchestration> logger)
        {
            _logger = logger;
        }

        public override async Task<string> RunTask(OrchestrationContext context, string input)
        {
            if (context.IsReplaying)
                _logger.LogInformation(">>>>> Orchestration replaying: {input} <<<<<", input);
            else
                _logger.LogInformation(">>>>> Orchestration: {input} <<<<<", input);

            var approvalTask = _approvalSource.Task;
            await approvalTask;

            return await context.ScheduleTask<string>(typeof(SayHelloActivity), input);
        }

        public override void OnEvent(OrchestrationContext context, string name, string input)
        {
            if (name.Equals("Approval"))
            {
                var rt = JsonConvert.DeserializeObject<ApprovalResult>(input);
                _approvalSource.TrySetResult(rt);
            }

            base.OnEvent(context, name, input);
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
