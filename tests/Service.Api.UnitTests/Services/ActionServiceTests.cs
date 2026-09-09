using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Service.Api.Exceptions.Item;
using Service.Api.Exceptions.Action;
using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Enums;
using Service.Api.Exceptions;
using Service.Api.Infrastructure.Database.Repositories.Action;
using Service.Api.Infrastructure.Cache.Action;
using Service.Api.Models;
using Service.Api.Services;
using Xunit;

namespace Service.Api.UnitTests.Services;

public class ActionServiceTests
{
    private readonly NullLogger<ActionService> _logger = NullLogger<ActionService>.Instance;
    private readonly List<string> _events = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    public async Task Invalid_names_fail_before_dependencies(string? name)
    {
        var service = new ActionService(new Repository(_events), new Cache(_events), _logger);
        var value = name == "too-long" ? new string('x', 201) : name!;
        await Assert.ThrowsAsync<RequestValidationException>(() => service.CreateAsync(
            new CreateActionRequest { ItemId = Guid.NewGuid(), Type = ActionType.Create, Name = value }));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(Guid.NewGuid(),
            new UpdateActionRequest { Name = value, Type = ActionType.Update }));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Undefined_enum_fails_before_dependencies()
    {
        var service = new ActionService(new Repository(_events), new Cache(_events), _logger);
        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(Guid.NewGuid(),
            new UpdateActionRequest { Name = "Valid", Type = (ActionType)99 }));
        await Assert.ThrowsAsync<RequestValidationException>(() => service.CreateAsync(
            new CreateActionRequest { ItemId = Guid.NewGuid(), Name = "Valid", Type = (ActionType)99 }));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Create_maps_saved_entity_without_populating_cache()
    {
        var repository = new Repository(_events);
        var service = new ActionService(repository, new Cache(_events), _logger);
        using var caller = new CancellationTokenSource();
        var parentId = Guid.NewGuid();
        var result = await service.CreateAsync(new CreateActionRequest { ItemId = parentId, Type = ActionType.Delete, Name = " Created " }, caller.Token);
        Assert.Equal(new[] { "parent", "create" }, _events);
        Assert.All(repository.Calls, call => Assert.Equal(caller.Token, call.Token));
        var created = Assert.Single(repository.Calls, call => call.Operation == "create");
        Assert.Same(repository.Entity, created.Value);
        Assert.Equal(repository.Entity!.Id, created.Id);
        Assert.Equal(parentId, repository.Calls[0].Id);
        Assert.Equal(parentId, repository.Entity.ItemId);
        Assert.Equal(ActionType.Delete, repository.Entity.Type);
        AssertValues(repository.Entity!, result);
        Assert.Equal(" Created ", result.Name);
        Assert.DoesNotContain("cache.set", _events);
    }

    [Fact]
    public async Task Cache_hit_avoids_entity_load()
    {
        using var cancellation = new CancellationTokenSource();
        var cached = new ActionDto { ItemId = Guid.NewGuid(), Id = Guid.NewGuid(), Name = "Cached", Type = ActionType.Update };
        var cache = new Cache(_events) { Value = cached };
        var repository = new Repository(_events);
        var result = await new ActionService(repository, cache, _logger).GetAsync(cached.Id, cancellation.Token);
        Assert.Same(cached, result);
        Assert.Equal(cached.Id, Assert.Single(cache.Calls).Id);
        Assert.Equal(cached.Id, Assert.Single(repository.Calls).Id);
        Assert.DoesNotContain("get", _events);
        Assert.DoesNotContain("cache.set", _events);
        Assert.Equal(cancellation.Token, cache.Token);
        Assert.Contains("exists", _events);
        Assert.Equal(cancellation.Token, repository.Token);
    }

    [Fact]
    public async Task Miss_loads_maps_and_populates_cache()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Repository(_events) { Entity = Example() };
        var cache = new Cache(_events);
        var result = await new ActionService(repository, cache, _logger).GetAsync(repository.Entity.Id, cancellation.Token);
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
        var service = new ActionService(new Repository(_events), new Cache(_events), _logger);
        var exception = await Assert.ThrowsAsync<ActionNotFoundException>(async () =>
        {
            if (operation == "get") await service.GetAsync(missingId);
            else if (operation == "delete") await service.DeleteAsync(missingId);
            else await service.UpdateAsync(missingId, new UpdateActionRequest { Name = "Updated", Type = ActionType.Update });
        });
        Assert.Equal(missingId, exception.ActionId);
        Assert.Equal("The requested Action was not found.", exception.Message);
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
        var originalParent = entity.ItemId;
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
        var service = new ActionService(repository, cache, _logger);
        async Task Run()
        {
            if (delete) await service.DeleteAsync(entity.Id, cancellation.Token);
            else
            {
                var result = await service.UpdateAsync(entity.Id,
                    new UpdateActionRequest { Name = "Updated", Type = ActionType.Update }, cancellation.Token);
                Assert.Equal("Updated", result.Name);
                Assert.Equal(ActionType.Update, result.Type);
                AssertValues(entity, result);
            }
        }
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(Run);
        else await Run();
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.All(repository.Calls, call => { Assert.Equal(entity.Id, call.Id); Assert.Equal(cancellation.Token, call.Token); });
        Assert.Same(entity, repository.Calls.Last().Value);
        Assert.Equal(originalParent, entity.ItemId);
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
        var service = new ActionService(repository, cache, _logger);
        Task work = delete ? service.DeleteAsync(entity.Id, caller.Token)
            : service.UpdateAsync(entity.Id, new UpdateActionRequest { Name = "Updated", Type = ActionType.Delete }, caller.Token);

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
        var work = new ActionService(repository, cache, _logger).GetAsync(entity.Id, caller.Token);
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
        var work = new ActionService(repository, cache, _logger).CreateAsync(new CreateActionRequest { Name = "Created", ItemId = Guid.NewGuid(), Type = ActionType.Create });
        await write.Started;
        Assert.False(work.IsCompleted);
        Assert.Null(repository.Entity);
        write.Complete();
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        AssertValues(repository.Entity!, result);
        Assert.Empty(cache.Calls);
    }

    [Fact]
    public async Task GetAsync_WhenStaleCleanupPending_AwaitsIndependentRemovalBeforeNotFound()
    {
        using var removal = new PendingCall();
        using var caller = new CancellationTokenSource();
        var entity = Example();
        var repository = new Repository(_events) { Entity = entity, Exists = false };
        var cache = new Cache(_events)
        {
            Value = new ActionDto { Id = entity.Id },
            OnCallAsync = (stage, token) => stage == "cache.remove" ? removal.InvokeAsync(token) : Task.CompletedTask
        };
        var work = new ActionService(repository, cache, _logger).GetAsync(entity.Id, caller.Token);
        await removal.Started;
        caller.Cancel();
        Assert.Equal(CancellationToken.None, removal.Token);
        Assert.False(work.IsCompleted);
        removal.Complete();
        var error = await Assert.ThrowsAsync<ActionNotFoundException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(entity.Id, error.ActionId);
        Assert.Equal(new[] { "cache.get", "exists", "cache.remove" }, _events);
    }

    [Fact]
    public async Task Lists_map_all_fields_without_using_cache()
    {
        var first = Example();
        var second = Example();
        var repository = new Repository(_events) { Entities = new[] { second, first } };
        using var caller = new CancellationTokenSource();
        var results = await new ActionService(repository, new Cache(_events), _logger).ListAsync(caller.Token);
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
        Assert.Empty(await new ActionService(repository, cache, _logger).ListAsync(caller.Token));
        Assert.Equal("list", Assert.Single(repository.Calls).Operation);
        Assert.Equal(caller.Token, repository.Calls[0].Token);
        Assert.Empty(cache.Calls);
    }

    [Theory]
    [InlineData("create", "parent", false)]
    [InlineData("create", "parent", true)]
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
    [InlineData("cached", "exists", false)]
    [InlineData("cached", "exists", true)]
    [InlineData("stale", "cache.remove", false)]
    [InlineData("parentlist", "parent", false)]
    [InlineData("parentlist", "parent", true)]
    [InlineData("parentlist", "list.parent", false)]
    [InlineData("parentlist", "list.parent", true)]
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
        if (operation is "cached" or "stale") cache.Value = new ActionDto { Id = entity.Id };
        if (operation == "stale") repository.Exists = false;
        var service = new ActionService(repository, cache, _logger);
        async Task Run()
        {
            switch (operation)
            {
                case "create": await service.CreateAsync(new CreateActionRequest { Name = "Created", ItemId = entity.ItemId, Type = ActionType.Delete }, caller.Token); break;
                case "list": await service.ListAsync(caller.Token); break;
                case "update": await service.UpdateAsync(entity.Id, new UpdateActionRequest { Name = "Updated", Type = ActionType.Delete }, caller.Token); break;
                case "delete": await service.DeleteAsync(entity.Id, caller.Token); break;
                case "parentlist": await service.ListByItemAsync(entity.ItemId, caller.Token); break;
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
            "create" => ["parent", "create"],
            "list" => ["list"],
            "update" => ["get.update", "update", "cache.remove"],
            "delete" => ["get.update", "delete", "cache.remove"],
            "cached" => ["cache.get", "exists"], "stale" => ["cache.get", "exists", "cache.remove"], "parentlist" => ["parent", "list.parent"],
            _ => ["cache.get", "get", "cache.set"]
        };
        Assert.Equal(expected.Take(Array.IndexOf(expected, stage) + 1), _events);
        foreach (var call in repository.Calls.Concat(cache.Calls))
        {
            Assert.Equal(call.Operation == "cache.remove" ? CancellationToken.None : caller.Token, call.Token);
            if (call.Operation is "parent" or "list.parent") Assert.Equal(entity.ItemId, call.Id);
            else if (call.Operation is not ("create" or "list")) Assert.Equal(entity.Id, call.Id);
            if (call.Operation is "update" or "delete") Assert.Same(entity, call.Value);
            if (call.Operation == "cache.set") AssertValues(entity, Assert.IsType<ActionDto>(call.Value));
        }
    }

    [Theory]
    [InlineData("get", false)] [InlineData("get", true)]
    [InlineData("update", true)] [InlineData("delete", true)]
    public async Task Operation_WhenCacheMissOrEntityMissing_LogsDebugContext(string operation, bool missing)
    {
        var id = Guid.NewGuid();
        var repository = new Repository(_events) { Entity = missing ? null : Example() };
        var logs = new RecordingLogger<ActionService>();
        var service = new ActionService(repository, new Cache(_events), logs);
        async Task Run()
        {
            if (operation == "get") await service.GetAsync(id);
            else if (operation == "delete") await service.DeleteAsync(id);
            else await service.UpdateAsync(id, new UpdateActionRequest { Name = "Updated", Type = ActionType.Delete });
        }
        if (missing) await Assert.ThrowsAsync<ActionNotFoundException>(Run); else await Run();
        Assert.Equal(operation == "get" && missing ? 2 : 1, logs.Entries.Count);
        Assert.All(logs.Entries, log =>
        {
            Assert.Equal(LogLevel.Debug, log.Level); Assert.Null(log.Exception);
            Assert.Equal("Action", log.State["Domain"]); Assert.Equal(id, log.State["ActionId"]);
        });
        if (operation == "get") Assert.Equal("{Domain}: No usable cached Action {ActionId}; loading from PostgreSQL.", logs.Entries[0].State["{OriginalFormat}"]);
        if (missing) Assert.Equal("{Domain}: Action {ActionId} was not found.", logs.Entries[^1].State["{OriginalFormat}"]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ListByItemAsync_WhenParentExists_PreservesEmptyOrOrderedResultsAndTokens(bool populated)
    {
        using var caller = new CancellationTokenSource(); var id = Guid.NewGuid();
        var first = Example(); var second = Example(); first.ItemId = second.ItemId = id;
        var repository = new Repository(_events) { Entities = populated ? new[] { second, first } : Array.Empty<ActionModel>() };
        var cache = new Cache(_events);
        var results = await new ActionService(repository, cache, _logger).ListByItemAsync(id, caller.Token);
        Assert.Equal(new[] { "parent", "list.parent" }, _events);
        Assert.All(repository.Calls, call => { Assert.Equal(id, call.Id); Assert.Equal(caller.Token, call.Token); });
        Assert.Empty(cache.Calls);
        if (populated) { Assert.Equal(2, results.Count); AssertValues(second, results[0]); AssertValues(first, results[1]); }
        else Assert.Empty(results);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateAsync_WhenParentMissingOrDisappears_LogsDebugParentContext(bool race)
    {
        var id = Guid.NewGuid(); var logs = new RecordingLogger<ActionService>();
        var repository = new Repository(_events) { ParentExists = race, CreateSucceeds = false };
        await Assert.ThrowsAsync<ItemNotFoundException>(() => new ActionService(repository, new Cache(_events), logs).CreateAsync(
            new CreateActionRequest { ItemId = id, Name = "Valid", Type = ActionType.Create }));
        var log = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Debug, log.Level); Assert.Null(log.Exception);
        Assert.Equal("Action", log.State["Domain"]); Assert.Equal(id, log.State["ItemId"]);
        Assert.Equal(race ? "{Domain}: Parent Item {ItemId} disappeared before Action creation." : "{Domain}: Parent Item {ItemId} was not found.", log.State["{OriginalFormat}"]);
    }

    private static ActionModel Example() => new()
    {
        ItemId = Guid.NewGuid(), Name = "Example", Type = ActionType.Update,
        CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow
    };

    private static void AssertValues(ActionModel expected, ActionDto actual)
    {
        Assert.Equal((expected.Id, expected.ItemId, expected.Name, expected.Type, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.ItemId, actual.Name, actual.Type, actual.CreatedAt, actual.UpdatedAt));
    }

    [Fact]
    public async Task Empty_parent_fails_before_dependencies()
    {
        var service = new ActionService(new Repository(_events), new Cache(_events), _logger);
        await Assert.ThrowsAsync<RequestValidationException>(() => service.CreateAsync(
            new CreateActionRequest { ItemId = Guid.Empty, Name = "Valid", Type = ActionType.Create }));
        Assert.Empty(_events);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_parent_or_create_race_becomes_parent_not_found(bool parentExists, bool createSucceeds)
    {
        var parentId = Guid.NewGuid();
        var repository = new Repository(_events) { ParentExists = parentExists, CreateSucceeds = createSucceeds };
        var service = new ActionService(repository, new Cache(_events), _logger);
        var exception = await Assert.ThrowsAsync<ItemNotFoundException>(() => service.CreateAsync(
            new CreateActionRequest { ItemId = parentId, Name = "Valid", Type = ActionType.Create }));
        Assert.Equal(parentId, exception.ItemId);
        Assert.Equal("The requested Item was not found.", exception.Message);
        Assert.DoesNotContain("cache.set", _events);
        Assert.Equal(parentExists, _events.Contains("create"));
    }

    [Fact]
    public async Task Stale_hit_is_removed_with_independent_token_and_returns_not_found()
    {
        using var cancellation = new CancellationTokenSource();
        var cached = new ActionDto { Id = Guid.NewGuid() };
        var repository = new Repository(_events) { Exists = false, OnCall = (operation, _) => { if (operation == "exists") cancellation.Cancel(); } };
        var cache = new Cache(_events) { Value = cached };
        var logger = new Logger();
        var exception = await Assert.ThrowsAsync<ActionNotFoundException>(() =>
            new ActionService(repository, cache, logger).GetAsync(cached.Id, cancellation.Token));
        Assert.Equal("The requested Action was not found.", exception.Message);
        Assert.Equal(new[] { "cache.get", "exists", "cache.remove" }, _events);
        Assert.Equal(CancellationToken.None, cache.Token);
        Assert.Equal(cached.Id, cache.RemovedId);
        Assert.Equal(cached.Id, exception.ActionId);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.Equal("{Domain}: Rejected stale cached Action {ActionId}; requesting cache removal.", log.Properties["{OriginalFormat}"]);
        Assert.Equal("Action", log.Properties["Domain"]);
        Assert.Equal(cached.Id, log.Properties["ActionId"]);
        Assert.Equal(cancellation.Token, repository.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_list_requires_parent_and_preserves_results(bool parentExists)
    {
        var entity = Example();
        var repository = new Repository(_events) { ParentExists = parentExists, Entities = new[] { entity } };
        var service = new ActionService(repository, new Cache(_events), _logger);
        if (!parentExists)
        {
            var exception = await Assert.ThrowsAsync<ItemNotFoundException>(() => service.ListByItemAsync(entity.ItemId));
            Assert.Equal(entity.ItemId, exception.ItemId);
            Assert.Equal(new[] { "parent" }, _events);
        }
        else
        {
            AssertValues(entity, Assert.Single(await service.ListByItemAsync(entity.ItemId)));
            Assert.Equal(entity.ItemId, repository.ParentId);
            Assert.Equal(new[] { "parent", "list.parent" }, _events);
        }
    }

    private sealed class Cache(List<string> events) : IActionCache
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

        public ActionDto? Value { get; set; }
        public CancellationToken Token { get; private set; }
        public Guid RemovedId { get; private set; }
        public async Task<ActionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("cache.get", cancellationToken, id);
            return Value;
        }
        public async Task SetAsync(ActionDto value, CancellationToken cancellationToken = default)
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

    private sealed class Repository(List<string> events) : IActionRepository
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

        public ActionModel? Entity { get; set; }
        public IReadOnlyList<ActionModel> Entities { get; set; } = Array.Empty<ActionModel>();
        public System.Action? Write { get; set; }
        public CancellationToken Token { get; private set; }
        public async Task<ActionModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("get", cancellationToken, id);
            return Entity;
        }
        public async Task<ActionModel?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("get.update", cancellationToken, id);
            return Entity;
        }
        public async Task<IReadOnlyList<ActionModel>> ListAsync(CancellationToken cancellationToken = default)
        {
            await RecordAsync("list", cancellationToken);
            return Entities;
        }
        public async Task UpdateAsync(ActionModel entity, CancellationToken cancellationToken = default)
        {
            await RecordAsync("update", cancellationToken, entity.Id, entity);
            Write?.Invoke();
        }
        public async Task DeleteAsync(ActionModel entity, CancellationToken cancellationToken = default)
        {
            await RecordAsync("delete", cancellationToken, entity.Id, entity);
            Write?.Invoke();
        }
        public bool ParentExists { get; set; } = true;
        public bool CreateSucceeds { get; set; } = true;
        public bool Exists { get; set; } = true;
        public Guid ParentId { get; private set; }
        public async Task<bool> TryCreateAsync(ActionModel entity, CancellationToken cancellationToken = default)
        {
            await RecordAsync("create", cancellationToken, entity.Id, entity);
            Entity = entity;
            entity.CreatedAt = entity.UpdatedAt = DateTime.UtcNow;
            return CreateSucceeds;
        }
        public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("exists", cancellationToken, id);
            return Exists;
        }
        public async Task<bool> ItemExistsAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("parent", cancellationToken, id);
            return ParentExists;
        }
        public async Task<IReadOnlyList<ActionModel>> ListByItemAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await RecordAsync("list.parent", cancellationToken, id);
            ParentId = id;
            return Entities;
        }
    }
    private sealed class Logger : ILogger<ActionService>
    {
        public List<(LogLevel Level, Dictionary<string, object?> Properties)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = ((IEnumerable<KeyValuePair<string, object?>>)state!)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Entries.Add((logLevel, properties));
        }
    }
}
