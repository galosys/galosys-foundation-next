using Microsoft.Extensions.DependencyInjection;
using NetServer.ConsoleApp;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChannelServer<SampleChannelServer>(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Any, 8000);
});
builder.Services.AddHostedService<SenderBootstrapper>();

var host = builder.Build();
host.Run();
