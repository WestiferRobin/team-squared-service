namespace Service.Api.IntegrationTests.Fixtures;

public static class TestInfrastructureConfiguration
{
    // Read only the dependency a test needs; PostgreSQL tests do not require Redis.
    public static string PostgresFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                "Set ConnectionStrings__Postgres to the Compose test database. " +
                "See README.md, or run --filter 'Category!=Postgres&Category!=Redis' for HTTP-only tests.");
    }
}
