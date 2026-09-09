using Microsoft.AspNetCore.Mvc;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Dtos.Action.Responses;
using Service.Api.Mappers;
using Service.Api.Services;

namespace Service.Api.Controllers;

[ApiController]
[Route("actions")]
public class ActionsController : ControllerBase
{
    private readonly IActionService _actionService;

    public ActionsController(IActionService actionService)
    {
        _actionService = actionService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActionResponse>>> List(
        CancellationToken cancellationToken)
    {
        var actionDtos = await _actionService.ListAsync(cancellationToken);
        var responses = actionDtos.Select(ActionMapper.ToResponse).ToArray();

        return Ok(responses);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ActionResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<ActionResponse>> Create(
        CreateActionRequest request,
        CancellationToken cancellationToken)
    {
        var actionDto = await _actionService.CreateAsync(request, cancellationToken);
        var response = ActionMapper.ToResponse(actionDto);

        return CreatedAtAction(
            nameof(Get),
            new { actionId = actionDto.Id },
            response);
    }

    [HttpGet("{actionId:guid}")]
    public async Task<ActionResult<ActionResponse>> Get(
        Guid actionId,
        CancellationToken cancellationToken)
    {
        var actionDto = await _actionService.GetAsync(actionId, cancellationToken);
        var response = ActionMapper.ToResponse(actionDto);

        return Ok(response);
    }

    [HttpPut("{actionId:guid}")]
    public async Task<ActionResult<ActionResponse>> Update(
        Guid actionId,
        UpdateActionRequest request,
        CancellationToken cancellationToken)
    {
        var actionDto = await _actionService.UpdateAsync(actionId, request, cancellationToken);
        var response = ActionMapper.ToResponse(actionDto);

        return Ok(response);
    }

    [HttpDelete("{actionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid actionId,
        CancellationToken cancellationToken)
    {
        await _actionService.DeleteAsync(actionId, cancellationToken);

        return NoContent();
    }
}
