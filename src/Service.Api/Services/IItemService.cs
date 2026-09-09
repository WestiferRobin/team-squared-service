using Service.Api.Dtos.Item.Requests;
using Service.Api.Dtos.Item;

namespace Service.Api.Services;

public interface IItemService
{
    Task<ItemDto> CreateAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default);

    Task<ItemDto> GetAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ItemDto>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ItemDto> UpdateAsync(
        Guid itemId,
        UpdateItemRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);
}
