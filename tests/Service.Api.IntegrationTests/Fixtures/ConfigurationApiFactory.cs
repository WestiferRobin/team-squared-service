using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace Service.Api.IntegrationTests.Fixtures;

// Load the application's files without shell, command-line, or user-secret overrides.
public sealed class ConfigurationApiFactory(string environment = "Production",
    IReadOnlyDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddJsonFile(Path.Combine(context.HostingEnvironment.ContentRootPath, "appsettings.json"), optional: false);
            configuration.AddJsonFile(Path.Combine(context.HostingEnvironment.ContentRootPath, $"appsettings.{environment}.json"), optional: true);
            if (overrides is not null) configuration.AddInMemoryCollection(overrides);
        });
    }
}
