using Service.Api.Dtos.Action.Requests;
using Service.Api.Dtos.Action;

namespace Service.Api.Services;

public interface IActionService
{
    Task<ActionDto> CreateAsync(
        CreateActionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionDto>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionDto>> ListByItemAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<ActionDto> GetAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);

    Task<ActionDto> UpdateAsync(
        Guid actionId,
        UpdateActionRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid actionId,
        CancellationToken cancellationToken = default);
}
