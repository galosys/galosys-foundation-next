using Castle.DynamicProxy;

namespace AopSample.ConsoleApp.Infrastructure
{
    internal class CatchLoggingAttribute : InterceptorAttribute
    {
        public override async ValueTask InterceptAsync(IAsyncInvocation invocation, IServiceProvider sp)
        {
            var factory = sp.GetRequiredService<ILoggerFactory>();
            var logger = factory.CreateLogger(invocation.Invocation.TargetType);
            logger.LogWarning(">>>>>> Proceed start <<<<<<");
           await Console.Out.WriteLineAsync(">>>>>> Proceed start <<<<<<");
            await invocation.ProceedAsync();
            logger.LogWarning(">>>>>> Proceed end <<<<<<");
        }
    }
}
