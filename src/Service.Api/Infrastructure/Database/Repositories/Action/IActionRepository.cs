using Service.Api.Models;

namespace Service.Api.Infrastructure.Database.Repositories.Action;

// Write methods commit all pending changes on the scoped context; do not stage unrelated changes.
public interface IActionRepository
{
    Task<bool> TryCreateAsync(
        ActionModel action,
        CancellationToken cancellationToken = default);

    Task<ActionModel?> GetByIdAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionModel>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ActionModel?> GetForUpdateAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        ActionModel action,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ActionModel action,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionModel>> ListByItemAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<bool> ItemExistsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);
}
