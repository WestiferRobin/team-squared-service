using Microsoft.Extensions.Logging.Abstractions;
using Service.Api.Exceptions.Item;
using Service.Api.Dtos.Item;
using Service.Api.Dtos.Item.Requests;
using Service.Api.Enums;
using Service.Api.Exceptions;
using Service.Api.Infrastructure.Database.Repositories.Item;
using Service.Api.Infrastructure.Cache.Item;
using Service.Api.Models;
using Service.Api.Services;
using Xunit;

namespace Service.Api.UnitTests.Services;

public class ItemServiceTests
{
    private readonly NullLogger<ItemService> _logger = NullLogger<ItemService>.Instance;
    private readonly List<string> _events = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    public async Task Invalid_names_fail_before_dependencies(string? name)
    {
        var service = new ItemService(new Repository(_events), new Cache(_events), _logger);
        var value = name == "too-long" ? new string('x', 201) : name!;
        await Assert.ThrowsAsync<RequestValidationException>(() => service.CreateAsync(
            new CreateItemRequest { Name = value }));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(Guid.NewGuid(),
            new UpdateItemRequest { Name = value, Status = ItemStatus.Archived }));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Undefined_enum_fails_before_dependencies()
    {
        var service = new ItemService(new Repository(_events), new Cache(_events), _logger);
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(Guid.NewGuid(),
            new UpdateItemRequest { Name = "Valid", Status = (ItemStatus)99 }));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Create_maps_saved_entity_without_populating_cache()
    {
        var repository = new Repository(_events);
        var service = new ItemService(repository, new Cache(_events), _logger);
        var result = await service.CreateAsync(new CreateItemRequest { Name = "Created" });
        AssertValues(repository.Entity!, result);
        Assert.Equal("Created", result.Name);
        Assert.DoesNotContain("cache.set", _events);
    }

    [Fact]
    public async Task Cache_hit_avoids_entity_load()
    {
        using var cancellation = new CancellationTokenSource();
        var cached = new ItemDto { Id = Guid.NewGuid(), Name = "Cached", Status = ItemStatus.Archived };
        var cache = new Cache(_events) { Value = cached };
        var repository = new Repository(_events);
        var result = await new ItemService(repository, cache, _logger).GetAsync(cached.Id, cancellation.Token);
        Assert.Same(cached, result);
        Assert.DoesNotContain("get", _events);
        Assert.DoesNotContain("cache.set", _events);
        Assert.Equal(cancellation.Token, cache.Token);
    }

    [Fact]
    public async Task Miss_loads_maps_and_populates_cache()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Repository(_events) { Entity = Example() };
        var cache = new Cache(_events);
        var result = await new ItemService(repository, cache, _logger).GetAsync(repository.Entity.Id, cancellation.Token);
        AssertValues(repository.Entity, result);
        Assert.Same(result, cache.Value);
        Assert.Equal(new[] { "cache.get", "get", "cache.set" }, _events);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(cancellation.Token, cache.Token);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Missing_entity_throws_without_cache_mutation(string operation)
    {
        var missingId = Guid.NewGuid();
        var service = new ItemService(new Repository(_events), new Cache(_events), _logger);
        var exception = await Assert.ThrowsAsync<ItemNotFoundException>(async () =>
        {
            if (operation == "get") await service.GetAsync(missingId);
            else if (operation == "delete") await service.DeleteAsync(missingId);
            else await service.UpdateAsync(missingId, new UpdateItemRequest { Name = "Updated", Status = ItemStatus.Archived });
        });
        Assert.Equal(missingId, exception.ItemId);
        Assert.Equal("The requested Item was not found.", exception.Message);
        Assert.DoesNotContain("cache.remove", _events);
        Assert.DoesNotContain("cache.set", _events);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Writes_invalidate_only_after_success_even_if_request_then_cancels(bool delete, bool fail)
    {
        using var cancellation = new CancellationTokenSource();
        var entity = Example();
        var repository = new Repository(_events)
        {
            Entity = entity,
            Write = () =>
            {
                Assert.DoesNotContain("cache.remove", _events);
                if (fail) throw new InvalidOperationException("save failed");
                cancellation.Cancel();
            }
        };
        var cache = new Cache(_events);
        var service = new ItemService(repository, cache, _logger);
        async Task Run()
        {
            if (delete) await service.DeleteAsync(entity.Id, cancellation.Token);
            else
            {
                var result = await service.UpdateAsync(entity.Id,
                    new UpdateItemRequest { Name = "Updated", Status = ItemStatus.Archived }, cancellation.Token);
                Assert.Equal("Updated", result.Name);
                Assert.Equal(ItemStatus.Archived, result.Status);
                AssertValues(entity, result);
            }
        }
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(Run);
        else await Run();
        Assert.Equal(cancellation.Token, repository.Token);
        if (fail) Assert.DoesNotContain("cache.remove", _events);
        else
        {
            Assert.Equal("cache.remove", _events.Last());
            Assert.Equal(CancellationToken.None, cache.Token);
            Assert.Equal(entity.Id, cache.RemovedId);
        }
    }

    [Fact]
    public async Task Lists_map_all_fields_without_using_cache()
    {
        var first = Example();
        var second = Example();
        var repository = new Repository(_events) { Entities = new[] { second, first } };
        var results = await new ItemService(repository, new Cache(_events), _logger).ListAsync();
        Assert.Equal(2, results.Count);
        AssertValues(second, results[0]);
        AssertValues(first, results[1]);
        Assert.Equal(new[] { "list" }, _events);
    }

    private static ItemModel Example() => new()
    {
        Name = "Example", Status = ItemStatus.Archived,
        CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow
    };

    private static void AssertValues(ItemModel expected, ItemDto actual)
    {
        Assert.Equal((expected.Id, expected.Name, expected.Status, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.Name, actual.Status, actual.CreatedAt, actual.UpdatedAt));
    }

    private sealed class Cache(List<string> events) : IItemCache
    {
        public ItemDto? Value { get; set; }
        public CancellationToken Token { get; private set; }
        public Guid RemovedId { get; private set; }
        public Task<ItemDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("cache.get"); return Task.FromResult(Value);
        }
        public Task SetAsync(ItemDto value, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("cache.set"); Value = value; return Task.CompletedTask;
        }
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("cache.remove"); RemovedId = id; return Task.CompletedTask;
        }
    }

    private sealed class Repository(List<string> events) : IItemRepository
    {
        public ItemModel? Entity { get; set; }
        public IReadOnlyList<ItemModel> Entities { get; set; } = Array.Empty<ItemModel>();
        public System.Action? Write { get; set; }
        public CancellationToken Token { get; private set; }
        public Task<ItemModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("get"); return Task.FromResult(Entity);
        }
        public Task<ItemModel?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("get.update"); return Task.FromResult(Entity);
        }
        public Task<IReadOnlyList<ItemModel>> ListAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("list"); return Task.FromResult(Entities);
        }
        public Task UpdateAsync(ItemModel entity, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("update"); Write?.Invoke(); return Task.CompletedTask;
        }
        public Task DeleteAsync(ItemModel entity, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("delete"); Write?.Invoke(); return Task.CompletedTask;
        }
        public Task CreateAsync(ItemModel entity, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("create"); Entity = entity;
            entity.CreatedAt = entity.UpdatedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
    }
}
