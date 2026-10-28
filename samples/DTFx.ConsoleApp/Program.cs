using DTFx.ConsoleApp;
using DurableTask.Emulator;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDurableTaskCore()
    .AddDurableTaskOrchestration(sp => new LocalOrchestrationService());

builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
