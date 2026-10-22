using MafSample.API.Domain;
using MafSample.API.Infrastructure;

namespace MafSample.API
{
    class InternalModule : IModule
    {
        public void ConfigureServices(IServiceCollection services, HostBuilderContext context)
        {
            //services.AddActuatorEFCore<ApplicationDbContext>();
            //services.AddEfCoreOutboxStore<ApplicationDbContext>();
            //services.AddEfCoreChatHistoryProvider<ApplicationDbContext>();

            //services.AddRedisChatHistoryProvider();
            //services.AddAgentRedisMemoryProvider(o =>
            //{
            //    o.MemoryExtractor = ctx => ctx.ResponseMessages
            //        .SelectMany(m => m.Contents)
            //        .OfType<TextContent>()
            //        .Select(t => new MemoryItem("last-response", t.Text))
            //        .Take(1);
            //});

            //services.AddAIAgent("default", (sp, key) =>
            //{
            //    var chatClient = sp.GetRequiredKeyedService<IChatClient>("Deepseek");
            //    return chatClient.AsAIAgent(
            //                    new ChatClientAgentOptions
            //                    {
            //                        Name = "default",
            //                        ChatOptions = new ChatOptions
            //                        {
            //                            Instructions = "你一个相当于c#语言创作者安德斯·海尔斯伯格那样的技术专家。",
            //                            //Tools = [AIFunctionFactory.Create(Tools.GetWeather)]
            //                        },
            //                        ChatHistoryProvider = sp.GetRequiredService<EfCoreChatHistoryProvider<ApplicationDbContext>>(),
            //                        //ChatHistoryProvider = sp.GetRequiredService<RedisChatHistoryProvider>(),
            //                        AIContextProviders = [sp.GetRequiredService<AgentRedisMemoryProvider>()],
            //                    });
            //})
            //    .WithEfCoreSessionStore<ApplicationDbContext>()
            //    ;


            //services.AddCompliance();
            services.AddCqrs();

            services.AddStateMachine<OrderState, OrderEvent, OrderContext>(builder =>
            {
                builder.ExternalTransition().From(OrderState.Pending).To(OrderState.Closed).On(OrderEvent.Close);
                builder.ExternalTransition().From(OrderState.Pending).To(OrderState.Finished).On(OrderEvent.Finish);
            }, "order");
        }
    }
}
