using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Service.Api.IntegrationTests.Fixtures;

public class PostgresFixtureTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cleanup_WhenStepsFail_AttemptsDatabaseCleanupAndPreservesErrors(bool hostFails, bool databaseFails)
    {
        var hostError = new InvalidOperationException("host disposal");
        var databaseError = new IOException("database cleanup");
        var calls = new List<string>();
        var error = await Record.ExceptionAsync(() => PostgresFixture.CleanupAsync(
            () => { calls.Add("host"); return hostFails ? Task.FromException(hostError) : Task.CompletedTask; },
            () => { calls.Add("database"); return databaseFails ? Task.FromException(databaseError) : Task.CompletedTask; }));
        Assert.Equal(new[] { "host", "database" }, calls);
        if (hostFails && databaseFails)
            Assert.Equal(new Exception[] { hostError, databaseError }, Assert.IsType<AggregateException>(error).InnerExceptions);
        else Assert.Same(hostFails ? hostError : databaseError, error);
    }

    [Theory]
    [Trait("Category", "Postgres")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Fixture_WhenHostFails_DropsOwnedDatabaseAndPreservesInitializationFailure(bool initializationFails, bool disposalFails)
    {
        var initializationError = new InvalidOperationException("controlled host initialization failure");
        var disposalError = new IOException("controlled host disposal failure");
        var fixture = new PostgresFixture(settings => new FailingFactory(settings,
            initializationFails ? initializationError : null, disposalFails ? disposalError : null));
        try
        {
            if (initializationFails)
            {
                var error = await Record.ExceptionAsync(() => fixture.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                if (disposalFails)
                    Assert.Equal(new Exception[] { initializationError, disposalError }, Assert.IsType<AggregateException>(error).InnerExceptions);
                else Assert.Same(initializationError, error);
            }
            else
            {
                await fixture.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(await DatabaseExists(fixture.CachePrefix));
                Assert.Same(disposalError, await Record.ExceptionAsync(() => fixture.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(15))));
            }
            Assert.False(await DatabaseExists(fixture.CachePrefix));
            Assert.True(await DatabaseExists("service_test")); // The dedicated admin database survives.
        }
        finally
        {
            // The controlled factory throws only on its first disposal; retry is safe.
            await fixture.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task<bool> DatabaseExists(string name)
    {
        await using var connection = new NpgsqlConnection(TestInfrastructureConfiguration.PostgresFromEnvironment());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.OpenAsync(deadline.Token);
        await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT FROM pg_database WHERE datname = @name)", connection);
        query.Parameters.AddWithValue("name", name);
        return (bool)(await query.ExecuteScalarAsync(deadline.Token))!;
    }

    private sealed class FailingFactory(IReadOnlyDictionary<string, string?> settings,
        Exception? initializationError, Exception? disposalError) : ApiFactory
    {
        private bool disposed;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            if (initializationError is not null)
                builder.ConfigureServices(_ => throw initializationError);
        }

        public override async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await base.DisposeAsync();
            if (disposalError is not null) throw disposalError;
        }
    }
}
