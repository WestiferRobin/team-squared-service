using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Service.Api.Infrastructure.Cache;

public sealed class RedisCache: ICache
{
    private readonly IDistributedCache _cache;
    private readonly JsonSerializerOptions _serializer;
    private readonly ILogger<RedisCache> logger;

    public RedisCache(IDistributedCache cache, IOptions<JsonOptions> json, ILogger<RedisCache> logger)
    {
        this._cache = cache;
        this._serializer = json.Value.JsonSerializerOptions;
        this.logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var bytes = await _cache.GetAsync(key, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return bytes is null ? null : JsonSerializer.Deserialize<T>(bytes, _serializer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Exception messages can contain endpoints or credentials; log only the type.
            logger.LogWarning("Cache read failed ({FailureType}); check Redis configuration/connectivity or cached JSON; falling back to PostgreSQL", ex.GetType().Name);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _serializer);
            await _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Cache write failed ({FailureType}); database result remains successful", ex.GetType().Name);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _cache.RemoveAsync(key, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Cache removal failed ({FailureType}); database result remains successful", ex.GetType().Name);
        }
    }
}
