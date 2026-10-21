using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Messaging;

namespace NatsSample.ConsoleApp.Infrastructure
{
    //internal class NatsConsumerBootstrapper
    //    : BackgroundService
    //{
    //    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    //    {
    //        var handlerTypes = DependencyContext.Default?.GetImplementTypes().Where(i => i.IsAssignableToGeneric(typeof(IMessageHandler<>)));

    //        foreach (var item in handlerTypes)
    //        {
    //            if (item.GetMethods())
    //            {

    //            }
    //        }
    //    }
    //}
}
