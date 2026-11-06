using NATS.Extensions.Microsoft.DependencyInjection;

var builder = Host.CreateDefaultBuilder(args);
builder.UseModularization();
builder.ConfigureServices((context, services) =>
{
    // 连接参数（地址、认证）由上游 NatsOpts 管理，从配置节读取，避免硬编码。
    var nats = context.Configuration.GetSection("Nats");

    services.AddNatsClient(natsOpts =>
    {
        natsOpts.ConfigureOptions(optsBuilder => optsBuilder.Configure(o => o.Opts = o.Opts with
        {
            Url = nats["Url"] ?? "nats://127.0.0.1:4222",
            AuthOpts = new NATS.Client.Core.NatsAuthOpts
            {
                Username = nats["Username"],
                Password = nats["Password"]
            }
        }));
    });

    // AddNats（消费者注册）由 UseModularization() 发现的 InternalModule 自动完成，无需重复调用。

    // UseModularization() 会重建日志提供程序，只保留 AlarmLoggerProvider，
    // 导致控制台日志（含消费者的日志）全部被丢弃。此处补回控制台日志提供程序。
    services.AddLogging(logging => logging.AddConsole());
});

var host = builder.Build();
host.Run();
