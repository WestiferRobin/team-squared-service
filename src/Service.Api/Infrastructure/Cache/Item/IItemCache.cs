using Service.Api.Dtos.Item;

namespace Service.Api.Infrastructure.Cache.Item;

public interface IItemCache
{
    Task<ItemDto?> GetAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        ItemDto item,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);
}
