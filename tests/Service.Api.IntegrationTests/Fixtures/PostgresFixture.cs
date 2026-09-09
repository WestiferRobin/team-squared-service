using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Service.Api.Infrastructure.Database;
using Xunit;

namespace Service.Api.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly Func<IReadOnlyDictionary<string, string?>, WebApplicationFactory<Program>> createFactory;
    public PostgresFixture() : this(settings => new ApiFactory().WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)))) { }
    internal PostgresFixture(Func<IReadOnlyDictionary<string, string?>, WebApplicationFactory<Program>> createFactory)
        => this.createFactory = createFactory;

    private string adminConnection = null!;
    private readonly string databaseName = $"service_test_{Guid.NewGuid():N}";
    private bool created;
    public string CachePrefix => databaseName;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var settings = new NpgsqlConnectionStringBuilder(TestInfrastructureConfiguration.PostgresFromEnvironment());
        if (settings.Database != "service_test")
            throw new InvalidOperationException("Use the dedicated service_test database as the test connection target.");

        settings.Pooling = false;
        adminConnection = settings.ConnectionString;
        try
        {
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin);
            await create.ExecuteNonQueryAsync();
            created = true;
            settings.Database = databaseName;
            var testConnection = settings.ConnectionString;
            Factory = createFactory(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = testConnection,
                ["ConnectionStrings:Redis"] = "",
                ["Cache:KeyPrefix"] = CachePrefix
            });
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            await db.Database.MigrateAsync();
        }
        catch (Exception initializationError)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Fixture initialization and cleanup failed.", initializationError, cleanupError);
            }
            throw;
        }
    }

    public async Task ResetItemsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.Items.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => CleanupAsync(
        async () => { if (Factory is not null) await Factory.DisposeAsync(); }, DropOwnedDatabaseAsync);

    // Keep both failures when necessary, and never let host disposal skip database cleanup.
    internal static async Task CleanupAsync(Func<Task> disposeHost, Func<Task> dropDatabase)
    {
        Exception? hostError = null;
        try { await disposeHost(); }
        catch (Exception error) { hostError = error; }
        try { await dropDatabase(); }
        catch (Exception databaseError)
        {
            if (hostError is not null)
                throw new AggregateException("Host disposal and owned database cleanup failed.", hostError, databaseError);
            throw;
        }
        if (hostError is not null) ExceptionDispatchInfo.Capture(hostError).Throw();
    }

    private async Task DropOwnedDatabaseAsync()
    {
        if (!created) return;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        // Only the randomly named database created by this fixture is removed.
        await using var drop = new NpgsqlCommand($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        created = false;
    }
}
