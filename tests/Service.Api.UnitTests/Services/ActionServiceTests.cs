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
        var result = await service.CreateAsync(new CreateActionRequest { ItemId = Guid.NewGuid(), Type = ActionType.Create, Name = "Created" });
        AssertValues(repository.Entity!, result);
        Assert.Equal("Created", result.Name);
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
        var results = await new ActionService(repository, new Cache(_events), _logger).ListAsync();
        Assert.Equal(2, results.Count);
        AssertValues(second, results[0]);
        AssertValues(first, results[1]);
        Assert.Equal(new[] { "list" }, _events);
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
        var repository = new Repository(_events) { Exists = false };
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
        public ActionDto? Value { get; set; }
        public CancellationToken Token { get; private set; }
        public Guid RemovedId { get; private set; }
        public Task<ActionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("cache.get"); return Task.FromResult(Value);
        }
        public Task SetAsync(ActionDto value, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("cache.set"); Value = value; return Task.CompletedTask;
        }
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("cache.remove"); RemovedId = id; return Task.CompletedTask;
        }
    }

    private sealed class Repository(List<string> events) : IActionRepository
    {
        public ActionModel? Entity { get; set; }
        public IReadOnlyList<ActionModel> Entities { get; set; } = Array.Empty<ActionModel>();
        public System.Action? Write { get; set; }
        public CancellationToken Token { get; private set; }
        public Task<ActionModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("get"); return Task.FromResult(Entity);
        }
        public Task<ActionModel?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("get.update"); return Task.FromResult(Entity);
        }
        public Task<IReadOnlyList<ActionModel>> ListAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("list"); return Task.FromResult(Entities);
        }
        public Task UpdateAsync(ActionModel entity, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("update"); Write?.Invoke(); return Task.CompletedTask;
        }
        public Task DeleteAsync(ActionModel entity, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("delete"); Write?.Invoke(); return Task.CompletedTask;
        }
        public bool ParentExists { get; set; } = true;
        public bool CreateSucceeds { get; set; } = true;
        public bool Exists { get; set; } = true;
        public Guid ParentId { get; private set; }
        public Task<bool> TryCreateAsync(ActionModel entity, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("create"); Entity = entity;
            entity.CreatedAt = entity.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult(CreateSucceeds);
        }
        public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("exists"); return Task.FromResult(Exists);
        }
        public Task<bool> ItemExistsAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; events.Add("parent"); return Task.FromResult(ParentExists);
        }
        public Task<IReadOnlyList<ActionModel>> ListByItemAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; ParentId = id; events.Add("list.parent"); return Task.FromResult(Entities);
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
