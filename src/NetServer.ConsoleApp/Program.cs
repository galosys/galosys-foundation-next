using NetServer.ConsoleApp;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ListenerBootstrapper>();
builder.Services.AddHostedService<SenderBootstrapper>();
builder.Services.AddSingleton(sp => new SampleChannelServer(new IPEndPoint(IPAddress.Any, 8000), sp.GetRequiredService<ILogger<SampleChannelServer>>()));

var host = builder.Build();
host.Run();
