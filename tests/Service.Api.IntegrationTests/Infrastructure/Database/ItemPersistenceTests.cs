using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Service.Api.Enums;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Database;

[Trait("Category", "Postgres")]
public class ItemPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Item_round_trips_updates_and_deletes_across_contexts()
    {
        var item = new ItemModel { Name = "Persistence example" };
        var before = DateTime.UtcNow.AddSeconds(-1);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }
        DateTime createdAt;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            var saved = await db.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal("Persistence example", saved.Name);
            Assert.Equal(ItemStatus.Active, saved.Status);
            Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
            Assert.InRange(saved.CreatedAt, before, DateTime.UtcNow);
            Assert.Equal(saved.CreatedAt, saved.UpdatedAt);
            createdAt = saved.CreatedAt;
            saved.Name = "Updated example";
            saved.Status = ItemStatus.Archived;
            saved.CreatedAt = DateTime.UtcNow.AddYears(-1);
            db.SaveChanges(); // Exercise the synchronous timestamp path too.
            Assert.Equal(createdAt, saved.CreatedAt);
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            var saved = await db.Items.SingleAsync(i => i.Id == item.Id);
            Assert.Equal("Updated example", saved.Name);
            Assert.Equal(ItemStatus.Archived, saved.Status);
            Assert.Equal(createdAt, saved.CreatedAt);
            Assert.True(saved.UpdatedAt >= saved.CreatedAt);
            db.Items.Remove(saved);
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            Assert.False(await db.Items.AnyAsync(i => i.Id == item.Id));
        }
    }

    [Theory]
    [InlineData(null, 0, "23502")]
    [InlineData("", 0, "23514")]
    [InlineData("   ", 0, "23514")]
    [InlineData("valid", 99, "23514")]
    [InlineData("too-long", 0, "22001")]
    public async Task PostgreSQL_enforces_item_constraints(string? name, int status, string sqlState)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        db.Items.Add(new ItemModel { Name = name == "too-long" ? new string('x', 201) : name!, Status = (ItemStatus)status });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

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
}
