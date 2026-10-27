using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetServer.ConsoleApp;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SampleChannelHandler>();
builder.Services.AddSingleton(sp =>
{
    var options = new ChannelServerOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 8000),
    };
    var handler = sp.GetRequiredService<SampleChannelHandler>();

    return new ChannelServer(
        options,
        pipeline => pipeline.Run(handler.HandleAsync),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IServiceScopeFactory>());
});
builder.Services.AddHostedService<ChannelServerRunner>();
builder.Services.AddHostedService<SenderBootstrapper>();

var host = builder.Build();
host.Run();
