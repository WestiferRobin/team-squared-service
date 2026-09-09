using Service.Api.Exceptions.Item;
using Service.Api.Exceptions.Action;
using System.ComponentModel.DataAnnotations;
using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Exceptions;
using Service.Api.Infrastructure.Database.Repositories.Action;
using Service.Api.Infrastructure.Cache.Action;
using Service.Api.Models;

namespace Service.Api.Services;

public class ActionService : IActionService
{
    private readonly IActionRepository _actionRepository;
    private readonly IActionCache _actionCache;
    private readonly ILogger<ActionService> _logger;

    public ActionService(
        IActionRepository actionRepository,
        IActionCache actionCache,
        ILogger<ActionService> logger)
    {
        _actionRepository = actionRepository;
        _actionCache = actionCache;
        _logger = logger;
    }

    public async Task<ActionDto> CreateAsync(
        CreateActionRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        await EnsureItemExistsAsync(request.ItemId, cancellationToken);
        var action = new ActionModel
        {
            ItemId = request.ItemId,
            Name = request.Name,
            Type = request.Type
        };

        if (!await _actionRepository.TryCreateAsync(action, cancellationToken))
        {
            _logger.LogDebug("{Domain}: Parent Item {ItemId} disappeared before Action creation.", "Action", request.ItemId);
            throw new ItemNotFoundException(request.ItemId);
        }

        return ToDto(action);
    }

    public async Task<ActionDto> GetAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        var cachedAction = await _actionCache.GetAsync(actionId, cancellationToken);
        if (cachedAction is not null)
        {
            // A parent deletion can cascade without removing this cached Action.
            if (await _actionRepository.ExistsAsync(actionId, cancellationToken))
            {
                return cachedAction;
            }

            _logger.LogDebug("{Domain}: Rejected stale cached Action {ActionId}; requesting cache removal.", "Action", actionId);
            await _actionCache.RemoveAsync(actionId, CancellationToken.None);
            throw new ActionNotFoundException(actionId);
        }

        _logger.LogDebug("{Domain}: No usable cached Action {ActionId}; loading from PostgreSQL.", "Action", actionId);
        var action = await _actionRepository.GetByIdAsync(actionId, cancellationToken);
        if (action is null)
        {
            _logger.LogDebug("{Domain}: Action {ActionId} was not found.", "Action", actionId);
            throw new ActionNotFoundException(actionId);
        }
        var actionDto = ToDto(action);
        await _actionCache.SetAsync(actionDto, cancellationToken);

        return actionDto;
    }

    public async Task<IReadOnlyList<ActionDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var actions = await _actionRepository.ListAsync(cancellationToken);
        return actions.Select(ToDto).ToArray();
    }

    public async Task<ActionDto> UpdateAsync(
        Guid actionId,
        UpdateActionRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var action = await GetActionOrThrowAsync(actionId, cancellationToken);
        action.Name = request.Name;
        action.Type = request.Type;
        await _actionRepository.UpdateAsync(action, cancellationToken);

        // Persistence succeeded; request cancellation must not prevent cache cleanup.
        await _actionCache.RemoveAsync(actionId, CancellationToken.None);
        return ToDto(action);
    }

    public async Task DeleteAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        var action = await GetActionOrThrowAsync(actionId, cancellationToken);
        await _actionRepository.DeleteAsync(action, cancellationToken);

        await _actionCache.RemoveAsync(actionId, CancellationToken.None);
    }

    public async Task<IReadOnlyList<ActionDto>> ListByItemAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        await EnsureItemExistsAsync(itemId, cancellationToken);
        var actions = await _actionRepository.ListByItemAsync(itemId, cancellationToken);
        return actions.Select(ToDto).ToArray();
    }

    private async Task<ActionModel> GetActionOrThrowAsync(
        Guid actionId,
        CancellationToken cancellationToken)
    {
        var action = await _actionRepository.GetForUpdateAsync(actionId, cancellationToken);
        if (action is null)
        {
            _logger.LogDebug("{Domain}: Action {ActionId} was not found.", "Action", actionId);
            throw new ActionNotFoundException(actionId);
        }

        return action;
    }

    private async Task EnsureItemExistsAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (!await _actionRepository.ItemExistsAsync(itemId, cancellationToken))
        {
            _logger.LogDebug("{Domain}: Parent Item {ItemId} was not found.", "Action", itemId);
            throw new ItemNotFoundException(itemId);
        }
    }

    private static ActionDto ToDto(ActionModel action)
    {
        return new ActionDto
        {
            Id = action.Id,
            ItemId = action.ItemId,
            Name = action.Name,
            Type = action.Type,
            CreatedAt = action.CreatedAt,
            UpdatedAt = action.UpdatedAt,
        };
    }

    private static void Validate(CreateActionRequest request)
    {
        ValidateAnnotations(request);

        if (request.ItemId == Guid.Empty)
        {
            throw new RequestValidationException("ItemId must be a non-empty UUID.");
        }
    }

    private static void Validate(UpdateActionRequest request)
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
