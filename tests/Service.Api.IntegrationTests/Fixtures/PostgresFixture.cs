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
            Factory = new ApiFactory().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = testConnection, ["ConnectionStrings:Redis"] = "", ["Cache:KeyPrefix"] = CachePrefix })));
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            await db.Database.MigrateAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task ResetItemsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.Items.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (!created) return;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        // Only the randomly named database created by this fixture is removed.
        await using var drop = new NpgsqlCommand($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        created = false;
    }
}
