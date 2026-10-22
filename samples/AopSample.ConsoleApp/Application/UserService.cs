using AopSample.ConsoleApp.Infrastructure;

namespace AopSample.ConsoleApp.Application
{
    [Service]
    public class UserService
    {
        [CatchLogging]
        public virtual async Task<string> SayHelloAsync()
        {
            await Task.Delay(19);
            return "Say";
        }
    }
}
