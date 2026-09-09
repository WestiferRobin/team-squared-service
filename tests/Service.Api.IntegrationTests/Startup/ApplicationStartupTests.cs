using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Xunit;
namespace Service.Api.IntegrationTests.Startup;

[Trait("Category", "Postgres")]
public class ApplicationStartupTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Startup_WhenDatabaseUnmigrated_DoesNotApplyMigrations()
    {
        // Roll back only this fixture's uniquely owned database before starting a fresh host.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.GetService<IMigrator>().MigrateAsync("0");
        try
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            using var factory = new ConfigurationApiFactory(overrides: new Dictionary<string, string?>
            { ["ConnectionStrings:Postgres"] = db.Database.GetConnectionString() });
            using var client = factory.CreateClient();
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
            using var ready = await client.GetAsync("/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        }
        finally { await db.Database.MigrateAsync(); }
    }
}
