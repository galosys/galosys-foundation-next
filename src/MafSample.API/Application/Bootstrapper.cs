using Galosys.Foundation.Core;
using MafSample.API.Domain;
using MafSample.API.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Messaging;
using Microsoft.Extensions.StateMachine;

namespace MafSample.API.Application
{
    /// <summary>
    /// 
    /// </summary>
    [HostedService]
    internal class Bootstrapper : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public Bootstrapper(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var sp = scope.ServiceProvider;
                var messageBus = sp.GetRequiredService<IMessageBus>();
                var db = sp.GetRequiredService<ApplicationDbContext>();
                //var samples = await db.Query<Sample>().ToListAsync();

                var smf = sp.GetRequiredService<IStateMachineFactory>();
                var sm = smf.Get<OrderState, OrderEvent, OrderContext>("order");
                var ctx = new OrderContext();
                //var rt = await sm.FireAsync(OrderState.Pending, OrderEvent.Close, ctx);
                var rt = await sm.FireAsync(OrderState.Pending, OrderEvent.Finish, ctx);

                var cmdBus = sp.GetRequiredService<ICommandBus>();
                var id = await cmdBus.SendAsync(new NotificationSendCmd());
                var qry = sp.GetRequiredService<ITodoQry>();
                var exe = sp.GetRequiredService<IExecutor<NotificationSendCmd>>();
                await exe.ExecuteAsync(new NotificationSendCmd());

                //db.Database.EnsureCreated();
                //using (var trans = await db.Database.BeginTransactionAsync())
                //{
                //    await bus.SendAsync("test_send", new MsgSampleInput());
                //    await trans.CommitAsync();
                //}
                //while (!stoppingToken.IsCancellationRequested)
                //{
                //    await bus.SendAsync("test_send", new MsgSampleInput());
                //    await db.SaveChangesAsync(stoppingToken);
                //}
            }
            await Task.Delay(10);
        }
    }
}
