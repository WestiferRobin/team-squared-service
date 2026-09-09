using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Service.Api.Dtos.Item;
using Service.Api.Dtos.Action;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache;
using Xunit;

namespace Service.Api.UnitTests.Infrastructure.Cache;

public class RedisCacheTests
{
    internal static RedisCache Adapter(FakeDistributedCache backing, ILogger<RedisCache>? logger = null)
    {
        var json = new JsonOptions();
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<ItemStatus>(JsonNamingPolicy.CamelCase, false));
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<ActionType>(JsonNamingPolicy.CamelCase, false));
        return new RedisCache(backing, Options.Create(json), logger ?? NullLogger<RedisCache>.Instance);
    }

    [Fact]
    public async Task Set_get_remove_preserve_DTO_JSON_TTL_and_tokens()
    {
        var backing = new FakeDistributedCache();
        var cache = Adapter(backing);
        var now = DateTime.UtcNow;
        var dto = new ItemDto { Id = Guid.NewGuid(), Name = "Example", Status = ItemStatus.Archived, CreatedAt = now, UpdatedAt = now };
        using var source = new CancellationTokenSource();
        await cache.SetAsync("test:item", dto, TimeSpan.FromSeconds(12), source.Token);
        Assert.Equal("test:item", backing.Key);
        Assert.Equal(source.Token, backing.Token);
        Assert.Equal(TimeSpan.FromSeconds(12), backing.Options!.AbsoluteExpirationRelativeToNow);
        Assert.Null(backing.Options.SlidingExpiration);
        Assert.Contains("\"status\":\"archived\"", Encoding.UTF8.GetString(backing.Value!));
        var result = await cache.GetAsync<ItemDto>("test:item", source.Token);
        AssertItemValues(dto, result);
        Assert.Equal(DateTimeKind.Utc, result!.CreatedAt.Kind);
        await cache.RemoveAsync("test:item", source.Token);
        Assert.Null(backing.Value);
        Assert.Equal(source.Token, backing.Token);
        Assert.Null(await cache.GetAsync<ItemDto>("test:item"));
    }

    [Fact]
    public async Task GetAsync_WhenJsonNull_ReturnsQuietMiss()
    {
        var logs = new CacheLogger();
        var cache = Adapter(new FakeDistributedCache { Value = Encoding.UTF8.GetBytes("null") }, logs);
        Assert.Null(await cache.GetAsync<ItemDto>("key"));
        Assert.Empty(logs.Entries);
    }

    [Theory]
    [InlineData("not-json")] // Malformed JSON.
    [InlineData("[]")]       // Valid JSON, wrong shape.
    [InlineData("{}")]       // Object missing required members.
    public async Task GetAsync_WhenPayloadInvalid_ReturnsMissWithSafeWarning(string payload)
    {
        var logs = new CacheLogger();
        var cache = Adapter(new FakeDistributedCache { Value = Encoding.UTF8.GetBytes(payload) }, logs);
        Assert.Null(await cache.GetAsync<ItemDto>("KEY_SENTINEL"));
        AssertSafeWarning(logs, "read", nameof(JsonException));
    }

    [Theory]
    [InlineData("status", "unknown")]
    [InlineData("name", null)]
    public async Task One_invalid_field_in_an_otherwise_valid_payload_is_a_miss(string field, string? replacement)
    {
        var backing = new FakeDistributedCache();
        var logs = new CacheLogger();
        var cache = Adapter(backing, logs);
        var dto = new ItemDto { Id = Guid.NewGuid(), Name = "Valid", Status = ItemStatus.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        await cache.SetAsync("key", dto, TimeSpan.FromSeconds(30));
        var valid = backing.Value!;
        AssertItemValues(dto, await cache.GetAsync<ItemDto>("key"));
        var payload = JsonNode.Parse(valid)!.AsObject();
        if (replacement is null) Assert.True(payload.Remove(field));
        else payload[field] = replacement;
        backing.Value = Encoding.UTF8.GetBytes(payload.ToJsonString());
        Assert.Null(await cache.GetAsync<ItemDto>("key"));
        AssertSafeWarning(logs, "read", nameof(JsonException));
        // Restoring the single-field mutation restores a hit: no other invalid field is involved.
        backing.Value = valid;
        AssertItemValues(dto, await cache.GetAsync<ItemDto>("key"));
    }

    [Theory]
    [InlineData("read", false)]
    [InlineData("write", false)]
    [InlineData("removal", false)]
    [InlineData("read", true)]
    [InlineData("write", true)]
    [InlineData("removal", true)]
    public async Task Provider_failure_falls_back_with_sanitized_warning(string operation, bool timeout)
    {
        const string message = "MESSAGE_SENTINEL redis://private-host:6379,password=SECRET_SENTINEL";
        var inner = new Exception("INNER_PROVIDER_SENTINEL");
        Exception failure = timeout ? new TimeoutException(message, inner) : new InvalidOperationException(message, inner);
        failure.Data["ProviderDetail"] = "DATA_SENTINEL";
        var logs = new CacheLogger();
        var cache = Adapter(new FakeDistributedCache { Failure = failure }, logs);
        Assert.Null(await Operate(cache, operation));
        AssertSafeWarning(logs, operation, failure.GetType().Name);
    }

    [Theory]
    [InlineData("read", true)]
    [InlineData("write", true)]
    [InlineData("removal", true)]
    [InlineData("read", false)]
    [InlineData("write", false)]
    [InlineData("removal", false)]
    public async Task In_flight_caller_cancellation_propagates_even_if_provider_ignores_it(string operation, bool observesCancellation)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backing = new FakeDistributedCache { PendingOperation = pending.Task, ObserveCancellation = observesCancellation };
        var logs = new CacheLogger();
        using var caller = new CancellationTokenSource();
        var work = Operate(Adapter(backing, logs), operation, caller.Token);
        try
        {
            await backing.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(work.IsCompleted);
            Assert.Equal(caller.Token, backing.Token);
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => work.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Empty(logs.Entries);
        }
        finally { pending.TrySetResult(); }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("removal")]
    public async Task Noncompleting_provider_is_bounded_and_falls_back_with_a_safe_timeout_warning(string operation)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backing = new FakeDistributedCache { PendingOperation = pending.Task, ObserveCancellation = false };
        var logs = new CacheLogger();
        var elapsed = Stopwatch.StartNew();
        var work = Operate(Adapter(backing, logs), operation);
        try
        {
            await backing.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(work.IsCompleted);
            Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(pending.Task.IsCompleted);
            // The production bound is fixed at two seconds; no test-only production setting is added.
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1.5));
            AssertSafeWarning(logs, operation, nameof(TimeoutException));
            var lateValue = Encoding.UTF8.GetBytes("late-provider-value");
            backing.Value = lateValue;
            pending.TrySetResult();
            await backing.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await work);
            AssertSafeWarning(logs, operation, nameof(TimeoutException));
            if (operation == "read") Assert.Same(lateValue, backing.Value);
            else if (operation == "write") Assert.Contains("PAYLOAD_SENTINEL", Encoding.UTF8.GetString(backing.Value!));
            else Assert.Null(backing.Value);
        }
        finally { pending.TrySetResult(); }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)]
    public async Task SetAsync_WhenTtlNonpositive_RejectsBeforeProviderIo(int seconds)
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Adapter(backing, logs).SetAsync("KEY_SENTINEL", new object(), TimeSpan.FromSeconds(seconds)));
        Assert.Empty(backing.Calls); Assert.Empty(logs.Entries);
    }
    [Fact]
    public async Task SetAsync_WhenValueNull_RejectsBeforeProviderIo()
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger();
        await Assert.ThrowsAsync<ArgumentNullException>(() => Adapter(backing, logs).SetAsync<object>("KEY_SENTINEL", null!, TimeSpan.FromSeconds(1)));
        Assert.Empty(backing.Calls); Assert.Empty(logs.Entries);
    }
    [Theory]
    [InlineData("read")] [InlineData("write")] [InlineData("removal")]
    public async Task Operation_WhenCallerAlreadyCancelled_DoesNotCallProvider(string operation)
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger();
        var token = new CancellationToken(true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operate(Adapter(backing, logs), operation, token));
        Assert.Equal(token, error.CancellationToken);
        Assert.Empty(backing.Calls); Assert.Empty(logs.Entries);
    }
    [Fact]
    public async Task GetAsync_WhenProviderReturnsNull_MissesWithoutWarning()
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger();
        Assert.Null(await Adapter(backing, logs).GetAsync<ItemDto>("KEY_SENTINEL"));
        Assert.Equal("get", Assert.Single(backing.Calls)); Assert.Empty(logs.Entries);
    }
    [Theory]
    [InlineData("read")] [InlineData("write")] [InlineData("removal")]
    public async Task Operation_WhenProviderCancelsActiveCaller_FallsBackWithSafeWarning(string operation)
    {
        using var caller = new CancellationTokenSource();
        var backing = new FakeDistributedCache { Failure = new OperationCanceledException("MESSAGE_SENTINEL") };
        var logs = new CacheLogger();
        Assert.Null(await Operate(Adapter(backing, logs), operation, caller.Token));
        Assert.False(caller.IsCancellationRequested);
        Assert.Single(backing.Calls); Assert.Equal(caller.Token, backing.Token);
        AssertSafeWarning(logs, operation, nameof(OperationCanceledException));
    }
    [Fact]
    public async Task SetAsync_WhenSerializationFails_WarnsWithoutProviderWrite()
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger();
        await Adapter(backing, logs).SetAsync("KEY_SENTINEL", new Unserializable(), TimeSpan.FromSeconds(1));
        Assert.Empty(backing.Calls);
        AssertSafeWarning(logs, "write", nameof(InvalidOperationException));
    }
    private sealed class Unserializable
    {
        public string Secret => throw new InvalidOperationException("MESSAGE_SENTINEL PAYLOAD_SENTINEL SECRET_SENTINEL");
    }
    [Theory]
    [InlineData("id")] [InlineData("itemId")] [InlineData("name")]
    [InlineData("type")] [InlineData("createdAt")] [InlineData("updatedAt")]
    [InlineData("invalid-type")]
    public async Task GetAsync_WhenActionHasOneInvalidMember_ReturnsMissWithSafeWarning(string member)
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger(); var cache = Adapter(backing, logs);
        var dto = new ActionDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "PAYLOAD_SENTINEL", Type = ActionType.Delete, CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow };
        await cache.SetAsync("KEY_SENTINEL", dto, TimeSpan.FromSeconds(1));
        var valid = backing.Value!;
        var control = (await cache.GetAsync<ActionDto>("KEY_SENTINEL"))!;
        Assert.Equal((dto.Id, dto.ItemId, dto.Name, dto.Type, dto.CreatedAt, dto.UpdatedAt), (control.Id, control.ItemId, control.Name, control.Type, control.CreatedAt, control.UpdatedAt));
        var payload = JsonNode.Parse(valid)!.AsObject();
        if (member == "invalid-type") payload["type"] = "SECRET_SENTINEL";
        else Assert.True(payload.Remove(member));
        backing.Value = Encoding.UTF8.GetBytes(payload.ToJsonString());
        Assert.Null(await cache.GetAsync<ActionDto>("KEY_SENTINEL"));
        AssertSafeWarning(logs, "read", nameof(JsonException));
        backing.Value = valid;
        Assert.NotNull(await cache.GetAsync<ActionDto>("KEY_SENTINEL"));
    }
    [Theory]
    [InlineData("id")] [InlineData("status")] [InlineData("createdAt")] [InlineData("updatedAt")]
    public async Task GetAsync_WhenItemRequiredMemberMissing_ReturnsMissWithSafeWarning(string member)
    {
        var backing = new FakeDistributedCache(); var logs = new CacheLogger(); var cache = Adapter(backing, logs);
        await cache.SetAsync("KEY_SENTINEL", new ItemDto(), TimeSpan.FromSeconds(1));
        Assert.NotNull(await cache.GetAsync<ItemDto>("KEY_SENTINEL"));
        var payload = JsonNode.Parse(backing.Value!)!.AsObject(); Assert.True(payload.Remove(member));
        backing.Value = Encoding.UTF8.GetBytes(payload.ToJsonString());
        Assert.Null(await cache.GetAsync<ItemDto>("KEY_SENTINEL"));
        AssertSafeWarning(logs, "read", nameof(JsonException));
    }

    private static async Task<object?> Operate(RedisCache cache, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "read": return await cache.GetAsync<ItemDto>("KEY_SENTINEL", token);
            case "write": await cache.SetAsync("KEY_SENTINEL", new { Secret = "PAYLOAD_SENTINEL" }, TimeSpan.FromSeconds(30), token); break;
            case "removal": await cache.RemoveAsync("KEY_SENTINEL", token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        return null;
    }

    private static void AssertSafeWarning(CacheLogger logs, string operation, string failureType)
    {
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"Cache {operation} failed", entry.Message);
        Assert.Equal(failureType, entry.State["FailureType"]);
        Assert.Null(entry.Exception);
        var allLoggedValues = entry.Message + string.Join(" ", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));
        foreach (var secret in new[] { "MESSAGE_SENTINEL", "private-host", "SECRET_SENTINEL", "INNER_PROVIDER_SENTINEL",
            "DATA_SENTINEL", "KEY_SENTINEL", "PAYLOAD_SENTINEL" })
            Assert.DoesNotContain(secret, allLoggedValues);
    }

    private sealed class CacheLogger : ILogger<RedisCache>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, Dictionary<string, object?> State)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }

    private static void AssertItemValues(ItemDto expected, ItemDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Id, expected.Name, expected.Status, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.Name, actual.Status, actual.CreatedAt, actual.UpdatedAt));
    }
}
