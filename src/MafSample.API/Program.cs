var builder = WebApplication.CreateBuilder(args);
builder.Host.UseModularization();

// Add services to the container.
//builder.Services.AddOpenApi();
var app = builder.Build();

//app.UseRouting();
// Configure the HTTP request pipeline.
//app.UseSecurityHeaders();
app.UseSwaggerGen();
app.UseAspNetCore();

//app.UseHttpsRedirection();
//app.UseAgentsHosting();

//app.UseAuthorization();

app.Run();
