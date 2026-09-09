using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Service.Api.Infrastructure.Database;

public sealed class PostgresHealthCheck(ServiceDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
            return HealthCheckResult.Unhealthy("PostgreSQL is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (!await db.Database.CanConnectAsync(timeout.Token))
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            if ((await db.Database.GetPendingMigrationsAsync(timeout.Token)).Any())
                return HealthCheckResult.Unhealthy("PostgreSQL migrations are pending.");

            // Verify the application table is accessible, not just the server.
            await db.Items.AsNoTracking().AnyAsync(timeout.Token);
            await db.Actions.AsNoTracking().AnyAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.");
        }
    }
}
