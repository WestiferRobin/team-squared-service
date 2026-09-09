using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Service.Api.Extensions;
using Service.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApi();
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddCache(builder.Configuration);

builder.Services.AddScoped<IItemService, ItemService>();
builder.Services.AddScoped<IActionService, ActionService>();

var app = builder.Build();

if (string.IsNullOrWhiteSpace(
    app.Configuration.GetConnectionString("Redis")))
{
    app.Logger.LogWarning(
        "ConnectionStrings:Redis is missing; caching is unavailable and readiness will be degraded.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Service.Api v1"));
}

app.MapControllers();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

public partial class Program { }
