using NATS.Extensions.Microsoft.DependencyInjection;

var builder = Host.CreateDefaultBuilder(args);
builder.UseModularization();
builder.ConfigureServices(services =>
{
    services.AddNatsClient(nats =>
    {
        nats.ConfigureOptions(builder => builder.Configure(o => o.Opts = o.Opts with
        {
            Url = "nats://127.0.0.1:4222",
            AuthOpts = new NATS.Client.Core.NatsAuthOpts
            {
                Username = "appuser",
                Password = "AppPass123"
            }
        }));
    });


});

var host = builder.Build();
host.Run();
