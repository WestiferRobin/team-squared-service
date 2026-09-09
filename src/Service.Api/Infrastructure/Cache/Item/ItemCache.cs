using Microsoft.Extensions.Options;
using Service.Api.Dtos.Item;

namespace Service.Api.Infrastructure.Cache.Item;

public class ItemCache : IItemCache
{
    private readonly ICache _cache;
    private readonly IOptions<CacheOptions> _cacheOptions;

    public ItemCache(ICache cache, IOptions<CacheOptions> cacheOptions)
    {
        _cache = cache;
        _cacheOptions = cacheOptions;
    }

    public Task<ItemDto?> GetAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return _cache.GetAsync<ItemDto>(GetCacheKey(itemId), cancellationToken);
    }

    public Task SetAsync(
        ItemDto item,
        CancellationToken cancellationToken = default)
    {
        return _cache.SetAsync(GetCacheKey(item.Id), item,
            TimeSpan.FromSeconds(_cacheOptions.Value.DefaultTtlSeconds), cancellationToken);
    }

    public Task RemoveAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return _cache.RemoveAsync(GetCacheKey(itemId), cancellationToken);
    }

    private string GetCacheKey(Guid itemId) =>
        $"{_cacheOptions.Value.KeyPrefix}:items:{itemId:D}";
}
