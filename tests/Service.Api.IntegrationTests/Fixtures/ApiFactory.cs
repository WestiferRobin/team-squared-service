using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Service.Api.IntegrationTests.Fixtures;

// Ordinary test hosts keep developer documentation disabled, even when a test
// selects Development to exercise environment-dependent exception behavior.
public class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenApi:Enabled"] = "false" }));
    }
}
