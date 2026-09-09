using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Responses;
using Microsoft.AspNetCore.Mvc;
using Service.Api.Controllers;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Enums;
using Service.Api.Services;
using Xunit;

namespace Service.Api.UnitTests.Controllers;

public class ActionsControllerTests
{
    [Theory]
    [InlineData("List")]
    [InlineData("Get")]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Delete")]
    public async Task Delegates_request_and_token_and_returns_expected_result(string operation)
    {
        var service = new RecordingActionService();
        var controller = new ActionsController(service);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var id = Guid.NewGuid();
        var create = new CreateActionRequest { Name = "Create", ItemId = Guid.NewGuid(), Type = ActionType.Create };
        var update = new UpdateActionRequest { Name = "Update", Type = ActionType.Update };
        switch (operation)
        {
            case "List": AssertMappedList(service.Results, Assert.IsType<OkObjectResult>((await controller.List(token)).Result).Value); break;
            case "Get": AssertMapped(service.Result, Assert.IsType<OkObjectResult>((await controller.Get(id, token)).Result).Value); break;
            case "Create":
                var created = Assert.IsType<CreatedAtActionResult>((await controller.Create(create, token)).Result);
                Assert.Equal(201, created.StatusCode);
                Assert.Equal("Get", created.ActionName);
                Assert.Equal(service.Result.Id, created.RouteValues!["actionId"]);
                AssertMapped(service.Result, created.Value);
                Assert.Same(create, service.Request);
                break;
            case "Update":
                AssertMapped(service.Result, Assert.IsType<OkObjectResult>((await controller.Update(id, update, token)).Result).Value);
                Assert.Same(update, service.Request);
                break;
            case "Delete": Assert.Equal(204, Assert.IsType<NoContentResult>(await controller.Delete(id, token)).StatusCode); break;
        }
        Assert.Equal(operation, service.Operation);
        Assert.Equal(1, service.Calls);
        Assert.Equal(token, service.Token);
        if (operation is "Get" or "Update" or "Delete") Assert.Equal(id, service.Id);
    }

    [Fact]
    public async Task List_WhenServiceReturnsEmpty_MaterializesEmptyResponseArray()
    {
        var service = new RecordingActionService { Results = Array.Empty<ActionDto>() };
        using var caller = new CancellationTokenSource();
        var controller = new ActionsController(service);
        var result = Assert.IsType<OkObjectResult>((await controller.List(caller.Token)).Result);
        Assert.Empty(Assert.IsType<ActionResponse[]>(result.Value));
        Assert.Equal(caller.Token, Assert.Single(service.History).Token);
    }
    [Theory]
    [InlineData("List", false)]
    [InlineData("List", true)]
    [InlineData("Get", false)]
    [InlineData("Get", true)]
    [InlineData("Create", false)]
    [InlineData("Create", true)]
    [InlineData("Update", false)]
    [InlineData("Update", true)]
    [InlineData("Delete", false)]
    [InlineData("Delete", true)]
    public async Task Operation_WhenServiceFails_PropagatesExactFailureAndArguments(string operation, bool cancelled)
    {
        using var caller = new CancellationTokenSource();
        if (cancelled) caller.Cancel();
        Exception failure = cancelled ? new OperationCanceledException(caller.Token) : new InvalidOperationException("Service failure");
        var service = new RecordingActionService { Failure = failure };
        var controller = new ActionsController(service);
        var id = Guid.NewGuid();
        var create = new CreateActionRequest { Name = "Create", ItemId = Guid.NewGuid(), Type = ActionType.Delete };
        var update = new UpdateActionRequest { Name = "Update", Type = ActionType.Delete };
        async Task Run()
        {
            switch (operation)
            {
                case "List": await controller.List(caller.Token); break;
                case "Get": await controller.Get(id, caller.Token); break;
                case "Create": await controller.Create(create, caller.Token); break;
                case "Update": await controller.Update(id, update, caller.Token); break;
                case "Delete": await controller.Delete(id, caller.Token); break;
            }
        }
        Assert.Same(failure, await Record.ExceptionAsync(Run));
        var call = Assert.Single(service.History);
        Assert.Equal(operation, call.Operation); Assert.Equal(caller.Token, call.Token);
        if (operation is "Get" or "Update" or "Delete") Assert.Equal(id, call.Id);
        if (operation == "Create") Assert.Same(create, call.Request);
        if (operation == "Update") Assert.Same(update, call.Request);
    }

    private static void AssertMapped(ActionDto expected, object? value)
    {
        var actual = Assert.IsType<ActionResponse>(value);
        Assert.Equal((expected.Id, expected.ItemId, expected.Name, expected.Type, expected.CreatedAt, expected.UpdatedAt), (actual.Id, actual.ItemId, actual.Name, actual.Type, actual.CreatedAt, actual.UpdatedAt));
    }

    private static void AssertMappedList(IReadOnlyList<ActionDto> expected, object? value)
    {
        var actual = Assert.IsAssignableFrom<IReadOnlyList<ActionResponse>>(value);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) AssertMapped(expected[i], actual[i]);
    }
}
