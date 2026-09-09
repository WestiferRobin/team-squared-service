using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Service.Api.Infrastructure.Cache;

public sealed class RedisHealthCheck: IHealthCheck
{
    private readonly IDistributedCache _cache;
    private readonly IOptions<CacheOptions> _options;

    public RedisHealthCheck(IDistributedCache cache, IOptions<CacheOptions> options)
    {
        this._cache = cache;
        this._options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.GetAsync($"{_options.Value.KeyPrefix}:readiness", cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return HealthCheckResult.Degraded("Redis is unavailable or unconfigured; PostgreSQL fallback is available.");
        }
    }
}
