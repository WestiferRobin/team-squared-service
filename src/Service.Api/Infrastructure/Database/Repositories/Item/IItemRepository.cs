using Service.Api.Models;

namespace Service.Api.Infrastructure.Database.Repositories.Item;

// Write methods commit all pending changes on the scoped context; do not stage unrelated changes.
public interface IItemRepository
{
    Task CreateAsync(
        ItemModel item,
        CancellationToken cancellationToken = default);

    Task<ItemModel?> GetByIdAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ItemModel>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ItemModel?> GetForUpdateAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        ItemModel item,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ItemModel item,
        CancellationToken cancellationToken = default);
}
