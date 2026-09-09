using Service.Api.Dtos.Action;

namespace Service.Api.Infrastructure.Cache.Action;

public interface IActionCache
{
    Task<ActionDto?> GetAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        ActionDto action,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);
}
