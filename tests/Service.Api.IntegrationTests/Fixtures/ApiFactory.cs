using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Service.Api.IntegrationTests.Fixtures;

// Ordinary hosts use a fixed baseline. Explicit WithWebHostBuilder overrides still
// apply afterwards; tests of environment defaults use ConfigurationApiFactory instead.
public class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.Sources.Clear();
            config.AddJsonFile(Path.Combine(context.HostingEnvironment.ContentRootPath, "appsettings.json"), optional: false);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenApi:Enabled"] = "false",
                ["Cache:KeyPrefix"] = $"service-test:{Guid.NewGuid():N}"
            });
        });
    }
}
