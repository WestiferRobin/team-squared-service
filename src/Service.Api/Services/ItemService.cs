using Service.Api.Exceptions.Item;
using System.ComponentModel.DataAnnotations;
using Service.Api.Dtos.Item;
using Service.Api.Dtos.Item.Requests;
using Service.Api.Exceptions;
using Service.Api.Infrastructure.Database.Repositories.Item;
using Service.Api.Infrastructure.Cache.Item;
using Service.Api.Models;

namespace Service.Api.Services;

public class ItemService : IItemService
{
    private readonly IItemRepository _itemRepository;
    private readonly IItemCache _itemCache;
    private readonly ILogger<ItemService> _logger;

    public ItemService(
        IItemRepository itemRepository,
        IItemCache itemCache,
        ILogger<ItemService> logger)
    {
        _itemRepository = itemRepository;
        _itemCache = itemCache;
        _logger = logger;
    }

    public async Task<ItemDto> CreateAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var item = new ItemModel { Name = request.Name };
        await _itemRepository.CreateAsync(item, cancellationToken);

        return ToDto(item);
    }

    public async Task<ItemDto> GetAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var cachedItem = await _itemCache.GetAsync(itemId, cancellationToken);
        if (cachedItem is not null)
        {
            return cachedItem;
        }

        _logger.LogDebug("{Domain}: No usable cached Item {ItemId}; loading from PostgreSQL.", "Item", itemId);
        var item = await _itemRepository.GetByIdAsync(itemId, cancellationToken);
        if (item is null)
        {
            _logger.LogDebug("{Domain}: Item {ItemId} was not found.", "Item", itemId);
            throw new ItemNotFoundException(itemId);
        }
        var itemDto = ToDto(item);
        await _itemCache.SetAsync(itemDto, cancellationToken);

        return itemDto;
    }

    public async Task<IReadOnlyList<ItemDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await _itemRepository.ListAsync(cancellationToken);
        return items.Select(ToDto).ToArray();
    }

    public async Task<ItemDto> UpdateAsync(
        Guid itemId,
        UpdateItemRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var item = await GetItemOrThrowAsync(itemId, cancellationToken);
        item.Name = request.Name;
        item.Status = request.Status;
        await _itemRepository.UpdateAsync(item, cancellationToken);

        // Persistence succeeded; request cancellation must not prevent cache cleanup.
        await _itemCache.RemoveAsync(itemId, CancellationToken.None);
        return ToDto(item);
    }

    public async Task DeleteAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var item = await GetItemOrThrowAsync(itemId, cancellationToken);
        await _itemRepository.DeleteAsync(item, cancellationToken);

        await _itemCache.RemoveAsync(itemId, CancellationToken.None);
    }

    private async Task<ItemModel> GetItemOrThrowAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = await _itemRepository.GetForUpdateAsync(itemId, cancellationToken);
        if (item is null)
        {
            _logger.LogDebug("{Domain}: Item {ItemId} was not found.", "Item", itemId);
            throw new ItemNotFoundException(itemId);
        }

        return item;
    }

    private static ItemDto ToDto(ItemModel item)
    {
        return new ItemDto
        {
            Id = item.Id,
            Name = item.Name,
            Status = item.Status,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
        };
    }

    private static void Validate(CreateItemRequest request)
    {
        ValidateAnnotations(request);
    }

    private static void Validate(UpdateItemRequest request)
    {
        ValidateAnnotations(request);
    }

    private static void ValidateAnnotations(object request)
    {
        // Reuse request rules for callers outside MVC.
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true))
        {
            throw new RequestValidationException(string.Join(" ", errors.Select(error => error.ErrorMessage)));
        }
    }
}
