using System.Text.RegularExpressions;

var builder = Host.CreateDefaultBuilder(args);
builder.UseModularization();
var host = builder.Build();
host.Run();


