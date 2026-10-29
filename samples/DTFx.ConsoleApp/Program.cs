using DTFx.ConsoleApp;
using DurableTask.Emulator;
using DurableTask.SqlServer;

var builder = Host.CreateApplicationBuilder(args);

var key = "Default";
var connectionString = builder.Configuration.GetConnectionString("Default");

builder.Services.AddDurableTaskCore()
    //.AddDurableTaskOrchestration(sp => new LocalOrchestrationService())
    .AddDurableTaskOrchestration(sp => new SqlOrchestrationService(new SqlOrchestrationServiceSettings(connectionString, key, "dtfx")))
    ;

builder.Services.AddHostedService<Worker>();
var host = builder.Build();
host.Run();
