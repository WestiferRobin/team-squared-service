using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Xunit;
namespace Service.Api.IntegrationTests.Health;
[Trait("Category", "Postgres")]
public class PostgresHealthCheckTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("")] [InlineData("   ")]
    [InlineData("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Pooling=false")]
    public async Task CheckHealthAsync_WhenConnectionMissingOrUnreachable_ReturnsUnhealthy(string connection)
    {
        await using var db = PostgresOperations.Context(connection);
        Assert.Equal(HealthStatus.Unhealthy, (await new PostgresHealthCheck(db).CheckHealthAsync(new())).Status);
    }
    [Fact]
    public async Task CheckHealthAsync_WhenMigrationsPending_RecoversAfterMigration()
    {
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("0");
            var result = await new PostgresHealthCheck(db).CheckHealthAsync(new());
            Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Equal("PostgreSQL migrations are pending.", result.Description);
            await AssertEndpoints(503, "Unhealthy");
        }
        finally { await db.Database.MigrateAsync(); }
        Assert.Equal(HealthStatus.Healthy, (await new PostgresHealthCheck(db).CheckHealthAsync(new())).Status);
    }
    [Theory]
    [InlineData("Items")] [InlineData("Actions")]
    public async Task CheckHealthAsync_WhenRequiredTableMissingWithCurrentHistory_RecoversAfterRestore(string table)
    {
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        // Rename only the required table out of the application namespace, retaining history and rows.
        await db.Database.ExecuteSqlRawAsync(table == "Items" ? "ALTER TABLE \"Items\" RENAME TO \"test_hidden_Items\"" : "ALTER TABLE \"Actions\" RENAME TO \"test_hidden_Actions\"");
        try
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var result = await new PostgresHealthCheck(db).CheckHealthAsync(new());
            Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Equal("PostgreSQL readiness check failed.", result.Description);
            await AssertEndpoints(503, "Unhealthy");
        }
        finally { await db.Database.ExecuteSqlRawAsync(table == "Items" ? "ALTER TABLE \"test_hidden_Items\" RENAME TO \"Items\"" : "ALTER TABLE \"test_hidden_Actions\" RENAME TO \"Actions\""); }
        Assert.Equal(HealthStatus.Healthy, (await new PostgresHealthCheck(db).CheckHealthAsync(new())).Status);
        await AssertEndpoints(200, "Degraded");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CheckHealthAsync_WhenConnectedQueryBlocked_DistinguishesBudgetAndCallerCancellation(bool callerCancellation)
    {
        var connection = PostgresOperations.Connection(fixture); var application = $"health_{Guid.NewGuid():N}";
        await using var db = PostgresOperations.Context(connection, application);
        await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("LOCK TABLE \"Items\" IN ACCESS EXCLUSIVE MODE", blocker, transaction); await command.ExecuteNonQueryAsync();
        using var caller = new CancellationTokenSource(); var elapsed = Stopwatch.StartNew();
        var work = new PostgresHealthCheck(db).CheckHealthAsync(new(), caller.Token);
        try
        {
            await PostgresOperations.WaitForLockAsync(connection, application); Assert.False(work.IsCompleted);
            if (callerCancellation)
            {
                caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                var result = await work.WaitAsync(TimeSpan.FromSeconds(7));
                Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Equal("PostgreSQL readiness check failed.", result.Description);
                Assert.False(caller.IsCancellationRequested); Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(2.5));
            }
        }
        finally { caller.Cancel(); await transaction.RollbackAsync(); try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
        Assert.Equal(HealthStatus.Healthy, (await new PostgresHealthCheck(db).CheckHealthAsync(new())).Status);
    }
    private async Task AssertEndpoints(int status, string body)
    {
        using var client = fixture.Factory.CreateClient();
        using var health = await client.GetAsync("/health"); using var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode); Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        Assert.Equal(status, (int)ready.StatusCode); Assert.Equal(body, await ready.Content.ReadAsStringAsync());
    }
}
