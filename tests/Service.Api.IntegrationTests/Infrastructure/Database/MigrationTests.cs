using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Xunit;
namespace Service.Api.IntegrationTests.Infrastructure.Database;
[Trait("Category", "Postgres")]
public class MigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migrations_are_current_repeatable_and_enable_readiness()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        Assert.EndsWith("_InitialCreate", Assert.Single(db.Database.GetMigrations()));
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        await db.Database.OpenConnectionAsync();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename";
            await using var reader = await command.ExecuteReaderAsync();
            var tables = new List<string>();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            Assert.Equal(new[] { "Actions", "Items", "__EFMigrationsHistory" }, tables);
        }
        await db.Database.CloseConnectionAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        using var client = fixture.Factory.CreateClient();
        using var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Database_without_migrations_is_unready_until_migrated()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        using var client = fixture.Factory.CreateClient();
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("0");
            using var ready = await client.GetAsync("/ready");
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            await db.Database.MigrateAsync();
        }
        using var recovered = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
    [Fact]
    public async Task MigrateAsync_WhenRolledBack_ReappliesCleanSchemaAndHistory()
    {
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("0");
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetPendingMigrationsAsync());
            await db.Database.MigrateAsync();
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(await db.Items.AnyAsync()); Assert.False(await db.Actions.AnyAsync());
            await db.Database.MigrateAsync();
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { await db.Database.MigrateAsync(); }
    }
}
