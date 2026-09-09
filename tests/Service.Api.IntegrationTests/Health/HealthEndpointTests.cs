using Service.Api.IntegrationTests.Fixtures;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Service.Api.IntegrationTests.Health;

public class HealthEndpointTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Pooling=false")]
    public async Task Missing_or_unavailable_postgres_is_unready_but_live(string connectionString)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString, ["ConnectionStrings:Redis"] = "" })));
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        using var ready = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
    }
}
