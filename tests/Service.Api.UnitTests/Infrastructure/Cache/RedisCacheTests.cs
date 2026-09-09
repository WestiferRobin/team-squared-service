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

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Unusable_payload_is_not_a_hit(string payload)
    {
        var cache = Adapter(new FakeDistributedCache { Value = Encoding.UTF8.GetBytes(payload) });
        Assert.Null(await cache.GetAsync<ItemDto>("key"));
    }

    [Fact]
    public async Task Provider_failures_are_misses_or_no_ops()
    {
        var cache = Adapter(new FakeDistributedCache { Fail = true });
        Assert.Null(await cache.GetAsync<ItemDto>("key"));
        await cache.SetAsync("key", new object(), TimeSpan.FromSeconds(1));
        await cache.RemoveAsync("key");
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var cache = Adapter(new FakeDistributedCache());
        var token = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync<ItemDto>("key", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync("key", new object(), TimeSpan.FromSeconds(1), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RemoveAsync("key", token));
    }

    [Fact]
    public async Task Nonpositive_TTL_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Adapter(new FakeDistributedCache()).SetAsync("key", new object(), TimeSpan.Zero));
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
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("removal")]
    public async Task Provider_warnings_include_operation_and_type_but_no_sensitive_details(string operation)
    {
        var failure = new InvalidOperationException("MESSAGE_SENTINEL redis://private-host:6379,password=SECRET_SENTINEL",
            new Exception("INNER_PROVIDER_SENTINEL"));
        failure.Data["ProviderDetail"] = "DATA_SENTINEL";
        var logs = new CacheLogger();
        var cache = Adapter(new FakeDistributedCache { Failure = failure }, logs);
        Assert.Null(await Operate(cache, operation));
        AssertSafeWarning(logs, operation, nameof(InvalidOperationException));
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
        }
        finally { pending.TrySetResult(); }
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
