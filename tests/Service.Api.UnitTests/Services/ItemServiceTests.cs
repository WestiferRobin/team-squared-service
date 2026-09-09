using Microsoft.Extensions.Logging;
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
        using var caller = new CancellationTokenSource();
        var result = await service.CreateAsync(new CreateItemRequest { Name = " Created " }, caller.Token);
        Assert.Equal(new[] { "create" }, _events);
        Assert.All(repository.Calls, call => Assert.Equal(caller.Token, call.Token));
        var created = Assert.Single(repository.Calls, call => call.Operation == "create");
        Assert.Same(repository.Entity, created.Value);
        Assert.Equal(repository.Entity!.Id, created.Id);
        AssertValues(repository.Entity!, result);
        Assert.Equal(" Created ", result.Name);
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
        Assert.Equal(cached.Id, Assert.Single(cache.Calls).Id);
        Assert.Empty(repository.Calls);
        Assert.DoesNotContain("get", _events);
        Assert.DoesNotContain("cache.set", _events);
        Assert.Equal(cancellation.Token, cache.Token);
        Assert.All(cache.Calls, call => Assert.Equal(cancellation.Token, call.Token));
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
        Assert.All(cache.Calls, call => Assert.Equal(repository.Entity.Id, call.Id));
        Assert.Equal(repository.Entity.Id, Assert.Single(repository.Calls).Id);
        Assert.Equal(new[] { "cache.get", "get", "cache.set" }, _events);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(cancellation.Token, cache.Token);
        Assert.All(cache.Calls, call => Assert.Equal(cancellation.Token, call.Token));
        Assert.All(repository.Calls, call => Assert.Equal(cancellation.Token, call.Token));
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
        Assert.All(repository.Calls, call => { Assert.Equal(entity.Id, call.Id); Assert.Equal(cancellation.Token, call.Token); });
        Assert.Same(entity, repository.Calls.Last().Value);
        if (fail) Assert.DoesNotContain("cache.remove", _events);
        else
        {
            Assert.Equal("cache.remove", _events.Last());
            Assert.Equal(CancellationToken.None, cache.Token);
            Assert.Equal(entity.Id, cache.RemovedId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_WhenPending_AwaitsCommitThenIndependentInvalidation(bool delete)
    {
        using var write = new PendingCall();
        using var removal = new PendingCall();
        using var caller = new CancellationTokenSource();
        var entity = Example();
        var repository = new Repository(_events)
        {
            Entity = entity,
            OnCallAsync = (stage, token) => stage == (delete ? "delete" : "update")
                ? write.InvokeAsync(token) : Task.CompletedTask
        };
        var cache = new Cache(_events)
        {
            OnCallAsync = (stage, token) => stage == "cache.remove"
                ? removal.InvokeAsync(token) : Task.CompletedTask
        };
        var service = new ItemService(repository, cache, _logger);
        Task work = delete ? service.DeleteAsync(entity.Id, caller.Token)
            : service.UpdateAsync(entity.Id, new UpdateItemRequest { Name = "Updated", Status = ItemStatus.Archived }, caller.Token);

        await write.Started;
        Assert.False(work.IsCompleted);
        Assert.Empty(cache.Calls);
        Assert.Equal(caller.Token, write.Token);
        write.Complete();
        await removal.Started;
        caller.Cancel(); // Persistence finished: cleanup must remain independent of the caller.
        Assert.Equal(CancellationToken.None, removal.Token);
        Assert.False(work.IsCompleted);
        Assert.Equal(entity.Id, Assert.Single(cache.Calls).Id);
        removal.Complete();
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "get.update", delete ? "delete" : "update", "cache.remove" }, _events);
    }

    [Fact]
    public async Task GetAsync_WhenCachePopulationPending_AwaitsPopulationBeforeReturning()
    {
        using var lookup = new PendingCall();
        using var population = new PendingCall();
        using var caller = new CancellationTokenSource();
        var entity = Example();
        var repository = new Repository(_events)
        {
            Entity = entity,
            OnCallAsync = (stage, token) => stage == "get" ? lookup.InvokeAsync(token) : Task.CompletedTask
        };
        var cache = new Cache(_events)
        {
            OnCallAsync = (stage, token) => stage == "cache.set" ? population.InvokeAsync(token) : Task.CompletedTask
        };
        var work = new ItemService(repository, cache, _logger).GetAsync(entity.Id, caller.Token);
        await lookup.Started;
        Assert.False(work.IsCompleted);
        Assert.DoesNotContain("cache.set", _events);
        lookup.Complete();
        await population.Started;
        Assert.False(work.IsCompleted);
        Assert.Equal(caller.Token, population.Token);
        population.Complete();
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        AssertValues(entity, result);
        Assert.Same(result, cache.Value);
        Assert.Equal(new[] { "cache.get", "get", "cache.set" }, _events);
    }

    [Fact]
    public async Task CreateAsync_WhenPersistencePending_DoesNotReturnUnsavedResult()
    {
        using var write = new PendingCall();
        var repository = new Repository(_events)
        {
            OnCallAsync = (stage, token) => stage == "create" ? write.InvokeAsync(token) : Task.CompletedTask
        };
        var cache = new Cache(_events);
        var work = new ItemService(repository, cache, _logger).CreateAsync(new CreateItemRequest { Name = "Created" });
        await write.Started;
        Assert.False(work.IsCompleted);
        Assert.Null(repository.Entity);
        write.Complete();
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        AssertValues(repository.Entity!, result);
        Assert.Empty(cache.Calls);
    }

    [Fact]
    public async Task Lists_map_all_fields_without_using_cache()
    {
        var first = Example();
        var second = Example();
        var repository = new Repository(_events) { Entities = new[] { second, first } };
        using var caller = new CancellationTokenSource();
        var results = await new ItemService(repository, new Cache(_events), _logger).ListAsync(caller.Token);
        Assert.Equal(caller.Token, Assert.Single(repository.Calls).Token);
        Assert.Equal(2, results.Count);
        AssertValues(second, results[0]);
        AssertValues(first, results[1]);
        Assert.Equal(new[] { "list" }, _events);
    }

    [Fact]
    public async Task ListAsync_WhenRepositoryEmpty_ReturnsEmptyWithoutCache()
    {
        using var caller = new CancellationTokenSource();
        var repository = new Repository(_events); var cache = new Cache(_events);
        Assert.Empty(await new ItemService(repository, cache, _logger).ListAsync(caller.Token));
        Assert.Equal("list", Assert.Single(repository.Calls).Operation);
        Assert.Equal(caller.Token, repository.Calls[0].Token);
        Assert.Empty(cache.Calls);
    }

    [Theory]
    [InlineData("create", "create", false)]
    [InlineData("create", "create", true)]
    [InlineData("get", "cache.get", false)]
    [InlineData("get", "cache.get", true)]
    [InlineData("get", "get", false)]
    [InlineData("get", "get", true)]
    [InlineData("get", "cache.set", false)]
    [InlineData("get", "cache.set", true)]
    [InlineData("list", "list", false)]
    [InlineData("list", "list", true)]
    [InlineData("update", "get.update", false)]
    [InlineData("update", "get.update", true)]
    [InlineData("update", "update", false)]
    [InlineData("update", "update", true)]
    [InlineData("update", "cache.remove", false)]
    [InlineData("delete", "get.update", false)]
    [InlineData("delete", "get.update", true)]
    [InlineData("delete", "delete", false)]
    [InlineData("delete", "delete", true)]
    [InlineData("delete", "cache.remove", false)]
    public async Task Operation_WhenDependencyFailsAfterSuspension_PropagatesAndStopsLaterCalls(string operation, string stage, bool cancelled)
    {
        using var caller = new CancellationTokenSource();
        using var pending = new PendingCall();
        var failure = new InvalidOperationException("Controlled asynchronous failure");
        Task OnCallAsync(string current, CancellationToken token) =>
            current == stage ? pending.InvokeAsync(token) : Task.CompletedTask;
        var entity = Example();
        var repository = new Repository(_events) { Entity = entity, OnCallAsync = OnCallAsync };
        var cache = new Cache(_events) { OnCallAsync = OnCallAsync };

        var service = new ItemService(repository, cache, _logger);
        async Task Run()
        {
            switch (operation)
            {
                case "create": await service.CreateAsync(new CreateItemRequest { Name = "Created" }, caller.Token); break;
                case "list": await service.ListAsync(caller.Token); break;
                case "update": await service.UpdateAsync(entity.Id, new UpdateItemRequest { Name = "Updated", Status = ItemStatus.Archived }, caller.Token); break;
                case "delete": await service.DeleteAsync(entity.Id, caller.Token); break;

                default: await service.GetAsync(entity.Id, caller.Token); break;
            }
        }
        var work = Run();
        await pending.Started;
        Assert.False(work.IsCompleted);
        if (cancelled)
        {
            Assert.Equal(caller.Token, pending.Token);
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => work.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            pending.Fail(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                () => work.WaitAsync(TimeSpan.FromSeconds(5))));
        }
        string[] expected = operation switch
        {
            "create" => ["create"],
            "list" => ["list"],
            "update" => ["get.update", "update", "cache.remove"],
            "delete" => ["get.update", "delete", "cache.remove"],

            _ => ["cache.get", "get", "cache.set"]
        };
        Assert.Equal(expected.Take(Array.IndexOf(expected, stage) + 1), _events);
        foreach (var call in repository.Calls.Concat(cache.Calls))
        {
            Assert.Equal(call.Operation == "cache.remove" ? CancellationToken.None : caller.Token, call.Token);
            if (call.Operation is not ("create" or "list")) Assert.Equal(entity.Id, call.Id);
            if (call.Operation is "update" or "delete") Assert.Same(entity, call.Value);
            if (call.Operation == "cache.set") AssertValues(entity, Assert.IsType<ItemDto>(call.Value));
        }
    }

    [Theory]
    [InlineData("get", false)] [InlineData("get", true)]
    [InlineData("update", true)] [InlineData("delete", true)]
    public async Task Operation_WhenCacheMissOrEntityMissing_LogsDebugContext(string operation, bool missing)
    {
        var id = Guid.NewGuid();
        var repository = new Repository(_events) { Entity = missing ? null : Example() };
        var logs = new RecordingLogger<ItemService>();
        var service = new ItemService(repository, new Cache(_events), logs);
        async Task Run()
        {
            if (operation == "get") await service.GetAsync(id);
            else if (operation == "delete") await service.DeleteAsync(id);
            else await service.UpdateAsync(id, new UpdateItemRequest { Name = "Updated", Status = ItemStatus.Archived });
        }
        if (missing) await Assert.ThrowsAsync<ItemNotFoundException>(Run); else await Run();
        Assert.Equal(operation == "get" && missing ? 2 : 1, logs.Entries.Count);
        Assert.All(logs.Entries, log =>
        {
            Assert.Equal(LogLevel.Debug, log.Level); Assert.Null(log.Exception);
            Assert.Equal("Item", log.State["Domain"]); Assert.Equal(id, log.State["ItemId"]);
        });
        if (operation == "get") Assert.Equal("{Domain}: No usable cached Item {ItemId}; loading from PostgreSQL.", logs.Entries[0].State["{OriginalFormat}"]);
        if (missing) Assert.Equal("{Domain}: Item {ItemId} was not found.", logs.Entries[^1].State["{OriginalFormat}"]);
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
        public List<(string Operation, Guid Id, object? Value, CancellationToken Token)> Calls { get; } = [];
        public System.Action<string, CancellationToken>? OnCall { get; set; }
        public Func<string, CancellationToken, Task>? OnCallAsync { get; set; }
        private async Task RecordAsync(string operation, CancellationToken token, Guid id = default, object? value = null)
        {
            Token = token; events.Add(operation); Calls.Add((operation, id, value, token));
            OnCall?.Invoke(operation, token);
            if (OnCallAsync is not null) await OnCallAsync(operation, token);
        }

        public ItemDto? Value { get; set; }
        public CancellationToken Token { get; private set; }
        public Guid RemovedId { get; private set; }
        public async Task<ItemDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("cache.get", cancellationToken, id);
            return Value;
        }
        public async Task SetAsync(ItemDto value, CancellationToken cancellationToken = default)
        {
            await RecordAsync("cache.set", cancellationToken, value.Id, value);
            Value = value;
        }
        public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("cache.remove", cancellationToken, id);
            RemovedId = id;
        }
    }

    private sealed class Repository(List<string> events) : IItemRepository
    {
        public List<(string Operation, Guid Id, object? Value, CancellationToken Token)> Calls { get; } = [];
        public System.Action<string, CancellationToken>? OnCall { get; set; }
        public Func<string, CancellationToken, Task>? OnCallAsync { get; set; }
        private async Task RecordAsync(string operation, CancellationToken token, Guid id = default, object? value = null)
        {
            Token = token; events.Add(operation); Calls.Add((operation, id, value, token));
            OnCall?.Invoke(operation, token);
            if (OnCallAsync is not null) await OnCallAsync(operation, token);
        }

        public ItemModel? Entity { get; set; }
        public IReadOnlyList<ItemModel> Entities { get; set; } = Array.Empty<ItemModel>();
        public System.Action? Write { get; set; }
        public CancellationToken Token { get; private set; }
        public async Task<ItemModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("get", cancellationToken, id);
            return Entity;
        }
        public async Task<ItemModel?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("get.update", cancellationToken, id);
            return Entity;
        }
        public async Task<IReadOnlyList<ItemModel>> ListAsync(CancellationToken cancellationToken = default)
        {
            await RecordAsync("list", cancellationToken);
            return Entities;
        }
        public async Task UpdateAsync(ItemModel entity, CancellationToken cancellationToken = default)
        {
            await RecordAsync("update", cancellationToken, entity.Id, entity);
            Write?.Invoke();
        }
        public async Task DeleteAsync(ItemModel entity, CancellationToken cancellationToken = default)
        {
            await RecordAsync("delete", cancellationToken, entity.Id, entity);
            Write?.Invoke();
        }
        public async Task CreateAsync(ItemModel entity, CancellationToken cancellationToken = default)
        {
            await RecordAsync("create", cancellationToken, entity.Id, entity);
            Entity = entity;
            entity.CreatedAt = entity.UpdatedAt = DateTime.UtcNow;
        }
    }
}
