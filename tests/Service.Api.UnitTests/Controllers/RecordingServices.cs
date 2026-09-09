using Service.Api.Dtos.Item.Requests;
using Service.Api.Dtos.Item;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Dtos.Action;
using Service.Api.Enums;
using Service.Api.Services;

namespace Service.Api.UnitTests.Controllers;

internal sealed class RecordingItemService : IItemService
{
    public ItemDto Result { get; } = new ItemDto { Id = Guid.NewGuid(), Name = "Result", Status = ItemStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
    public IReadOnlyList<ItemDto> Results { get; } = new[] { new ItemDto { Id = Guid.NewGuid(), Name = "First", Status = ItemStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }, new ItemDto { Id = Guid.NewGuid(), Name = "Second", Status = ItemStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow } };
    public string? Operation { get; private set; }
    public Guid Id { get; private set; }
    public object? Request { get; private set; }
    public CancellationToken Token { get; private set; }
    public int Calls { get; private set; }
    private void Record(string operation, CancellationToken token, Guid id = default, object? request = null)
    { Operation = operation; Token = token; Id = id; Request = request; Calls++; }
    public Task<ItemDto> CreateAsync(CreateItemRequest request, CancellationToken cancellationToken = default)
    { Record("Create", cancellationToken, request: request); return Task.FromResult(Result); }
    public Task<ItemDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    { Record("Get", cancellationToken, id); return Task.FromResult(Result); }
    public Task<IReadOnlyList<ItemDto>> ListAsync(CancellationToken cancellationToken = default)
    { Record("List", cancellationToken); return Task.FromResult(Results); }
    public Task<ItemDto> UpdateAsync(Guid id, UpdateItemRequest request, CancellationToken cancellationToken = default)
    { Record("Update", cancellationToken, id, request); return Task.FromResult(Result); }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    { Record("Delete", cancellationToken, id); return Task.CompletedTask; }
}

internal sealed class RecordingActionService : IActionService
{
    public ActionDto Result { get; } = new ActionDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Result", Type = ActionType.Create, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
    public IReadOnlyList<ActionDto> Results { get; } = new[] { new ActionDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "First", Type = ActionType.Create, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }, new ActionDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Second", Type = ActionType.Create, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow } };
    public string? Operation { get; private set; }
    public Guid Id { get; private set; }
    public object? Request { get; private set; }
    public CancellationToken Token { get; private set; }
    public int Calls { get; private set; }
    private void Record(string operation, CancellationToken token, Guid id = default, object? request = null)
    { Operation = operation; Token = token; Id = id; Request = request; Calls++; }
    public Task<ActionDto> CreateAsync(CreateActionRequest request, CancellationToken cancellationToken = default)
    { Record("Create", cancellationToken, request: request); return Task.FromResult(Result); }
    public Task<ActionDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    { Record("Get", cancellationToken, id); return Task.FromResult(Result); }
    public Task<IReadOnlyList<ActionDto>> ListAsync(CancellationToken cancellationToken = default)
    { Record("List", cancellationToken); return Task.FromResult(Results); }
    public Task<ActionDto> UpdateAsync(Guid id, UpdateActionRequest request, CancellationToken cancellationToken = default)
    { Record("Update", cancellationToken, id, request); return Task.FromResult(Result); }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    { Record("Delete", cancellationToken, id); return Task.CompletedTask; }
    public Task<IReadOnlyList<ActionDto>> ListByItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    { Record("ListByItem", cancellationToken, itemId); return Task.FromResult(Results); }
}
