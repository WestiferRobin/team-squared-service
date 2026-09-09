using Microsoft.Extensions.Options;
using Service.Api.Dtos.Action;

namespace Service.Api.Infrastructure.Cache.Action;

public class ActionCache : IActionCache
{
    private readonly ICache _cache;
    private readonly IOptions<CacheOptions> _cacheOptions;

    public ActionCache(ICache cache, IOptions<CacheOptions> cacheOptions)
    {
        _cache = cache;
        _cacheOptions = cacheOptions;
    }

    public Task<ActionDto?> GetAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        return _cache.GetAsync<ActionDto>(GetCacheKey(actionId), cancellationToken);
    }

    public Task SetAsync(
        ActionDto action,
        CancellationToken cancellationToken = default)
    {
        return _cache.SetAsync(GetCacheKey(action.Id), action,
            TimeSpan.FromSeconds(_cacheOptions.Value.DefaultTtlSeconds), cancellationToken);
    }

    public Task RemoveAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        return _cache.RemoveAsync(GetCacheKey(actionId), cancellationToken);
    }

    private string GetCacheKey(Guid actionId) =>
        $"{_cacheOptions.Value.KeyPrefix}:actions:{actionId:D}";
}
