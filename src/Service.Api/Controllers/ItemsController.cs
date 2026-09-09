using Microsoft.AspNetCore.Mvc;
using Service.Api.Dtos.Item.Requests;
using Service.Api.Dtos.Item.Responses;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Dtos.Action.Responses;
using Service.Api.Mappers;
using Service.Api.Services;

namespace Service.Api.Controllers;

[ApiController]
[Route("items")]
public class ItemsController : ControllerBase
{
    private readonly IItemService _itemService;
    private readonly IActionService _actionService;

    public ItemsController(
        IItemService itemService,
        IActionService actionService)
    {
        _itemService = itemService;
        _actionService = actionService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ItemResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<ItemResponse>> Create(
        CreateItemRequest request,
        CancellationToken cancellationToken)
    {
        var itemDto = await _itemService.CreateAsync(request, cancellationToken);
        var response = ItemMapper.ToResponse(itemDto);

        return CreatedAtAction(
            nameof(Get),
            new { itemId = itemDto.Id },
            response);
    }

    [HttpGet("{itemId:guid}")]
    public async Task<ActionResult<ItemResponse>> Get(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var itemDto = await _itemService.GetAsync(itemId, cancellationToken);
        var response = ItemMapper.ToResponse(itemDto);

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ItemResponse>>> List(
        CancellationToken cancellationToken)
    {
        var itemDtos = await _itemService.ListAsync(cancellationToken);
        var responses = itemDtos.Select(ItemMapper.ToResponse).ToArray();

        return Ok(responses);
    }

    [HttpPut("{itemId:guid}")]
    public async Task<ActionResult<ItemResponse>> Update(
        Guid itemId,
        UpdateItemRequest request,
        CancellationToken cancellationToken)
    {
        var itemDto = await _itemService.UpdateAsync(itemId, request, cancellationToken);
        var response = ItemMapper.ToResponse(itemDto);

        return Ok(response);
    }

    [HttpDelete("{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await _itemService.DeleteAsync(itemId, cancellationToken);

        return NoContent();
    }

    [HttpGet("{itemId:guid}/actions")]
    public async Task<ActionResult<IReadOnlyList<ActionResponse>>> ListActions(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var actionDtos = await _actionService.ListByItemAsync(itemId, cancellationToken);
        var responses = actionDtos.Select(ActionMapper.ToResponse).ToArray();

        return Ok(responses);
    }

    [HttpPost("{itemId:guid}/actions")]
    [ProducesResponseType(typeof(ActionResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<ActionResponse>> CreateAction(
        Guid itemId,
        CreateItemActionRequest request,
        CancellationToken cancellationToken)
    {
        var createRequest = new CreateActionRequest
        {
            ItemId = itemId,
            Name = request.Name,
            Type = request.Type
        };

        var actionDto = await _actionService.CreateAsync(createRequest, cancellationToken);
        var response = ActionMapper.ToResponse(actionDto);

        return CreatedAtAction(
            nameof(ActionsController.Get),
            "Actions",
            new { actionId = actionDto.Id },
            response);
    }
}
