using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Responses;
using Service.Api.Dtos.Item;
using Service.Api.Dtos.Item.Responses;
using Microsoft.AspNetCore.Mvc;
using Service.Api.Controllers;
using Service.Api.Dtos.Item.Requests;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Enums;
using Service.Api.Services;
using Xunit;

namespace Service.Api.UnitTests.Controllers;

public class ItemsControllerTests
{
    [Theory]
    [InlineData("List")]
    [InlineData("Get")]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Delete")]
    public async Task Delegates_request_and_token_and_returns_expected_result(string operation)
    {
        var service = new RecordingItemService();
        var controller = new ItemsController(service, new RecordingActionService());
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var id = Guid.NewGuid();
        var create = new CreateItemRequest { Name = "Create",  };
        var update = new UpdateItemRequest { Name = "Update", Status = ItemStatus.Archived };
        switch (operation)
        {
            case "List": AssertMappedList(service.Results, Assert.IsType<OkObjectResult>((await controller.List(token)).Result).Value); break;
            case "Get": AssertMapped(service.Result, Assert.IsType<OkObjectResult>((await controller.Get(id, token)).Result).Value); break;
            case "Create":
                var created = Assert.IsType<CreatedAtActionResult>((await controller.Create(create, token)).Result);
                Assert.Equal(201, created.StatusCode);
                Assert.Equal("Get", created.ActionName);
                Assert.Equal(service.Result.Id, created.RouteValues!["itemId"]);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_routes_forward_parent_and_use_canonical_action_target(bool create)
    {
        var actions = new RecordingActionService();
        var items = new RecordingItemService();
        var controller = new ItemsController(items, actions);
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        if (create)
        {
            var request = new CreateItemActionRequest { Name = "Nested", Type = ActionType.Delete };
            var result = Assert.IsType<CreatedAtActionResult>((await controller.CreateAction(id, request, cancellation.Token)).Result);
            Assert.Equal(201, result.StatusCode);
            Assert.Equal("Actions", result.ControllerName);
            Assert.Equal("Get", result.ActionName);
            Assert.Equal(actions.Result.Id, result.RouteValues!["actionId"]);
            AssertMapped(actions.Result, result.Value);
            var forwarded = Assert.IsType<CreateActionRequest>(actions.Request);
            Assert.Equal(id, forwarded.ItemId);
            Assert.Equal(request.Name, forwarded.Name);
            Assert.Equal(request.Type, forwarded.Type);
            Assert.Equal("Create", actions.Operation);
        }
        else
        {
            AssertMappedList(actions.Results, Assert.IsType<OkObjectResult>((await controller.ListActions(id, cancellation.Token)).Result).Value);
            Assert.Equal(id, actions.Id);
            Assert.Equal("ListByItem", actions.Operation);
        }
        Assert.Equal(cancellation.Token, actions.Token);
        Assert.Equal(1, actions.Calls);
        Assert.Equal(0, items.Calls);
    }

    private static void AssertMapped(ItemDto expected, object? value)
    {
        var actual = Assert.IsType<ItemResponse>(value);
        Assert.Equal((expected.Id, expected.Name, expected.Status, expected.CreatedAt, expected.UpdatedAt), (actual.Id, actual.Name, actual.Status, actual.CreatedAt, actual.UpdatedAt));
    }

    private static void AssertMappedList(IReadOnlyList<ItemDto> expected, object? value)
    {
        var actual = Assert.IsAssignableFrom<IReadOnlyList<ItemResponse>>(value);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) AssertMapped(expected[i], actual[i]);
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
