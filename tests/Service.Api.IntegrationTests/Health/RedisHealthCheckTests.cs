using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Service.Api.Infrastructure.Cache;
using Service.Api.IntegrationTests.Fixtures;
using StackExchange.Redis;
using Xunit;
using ProviderCache = Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache;
namespace Service.Api.IntegrationTests.Health;
[Trait("Category", "Redis")]
public class RedisHealthCheckTests
{
    [Fact]
    public async Task Observation_WhenRearmed_IgnoresHistoricalTrafficAndRequiresFreshMatch()
    {
        await using var proxy = new RedisProxy(Infrastructure.Cache.RedisCacheTests.Connection());
        using var redis = await ConnectionMultiplexer.ConnectAsync(proxy.Connection);
        var database = redis.GetDatabase();
        var key = $"service-test:{Guid.NewGuid():N}:observation";
        var first = proxy.ObserveRequestsFor(key);
        await database.KeyExistsAsync(key).WaitAsync(TimeSpan.FromSeconds(5));
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        var fresh = proxy.ObserveRequestsFor(key);
        // A completed unrelated round-trip is a deterministic traffic barrier, not a sleep.
        await database.KeyExistsAsync($"service-test:{Guid.NewGuid():N}:unrelated")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fresh.IsCompleted);
        await database.KeyExistsAsync(key).WaitAsync(TimeSpan.FromSeconds(5));
        await fresh.WaitAsync(TimeSpan.FromSeconds(5));
        // EXISTS only: this regression creates no keys to clean up.
    }

    [Fact]
    public async Task CheckHealthAsync_WhenProbeKeyScoped_ReadsWithoutCreatingOrRemovingKeys()
    {
        var connection = Infrastructure.Cache.RedisCacheTests.Connection();
        using var redis = await ConnectionMultiplexer.ConnectAsync(connection); var database = redis.GetDatabase();
        var prefix = $"service-test:{Guid.NewGuid():N}"; var key = prefix + ":readiness"; var unrelated = prefix + ":unrelated";
        using var provider = new ProviderCache(Options.Create(new RedisCacheOptions { Configuration = connection }));
        var health = new RedisHealthCheck(provider, Options.Create(new CacheOptions { KeyPrefix = prefix }));
        try
        {
            await database.StringSetAsync(unrelated, "unchanged").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
            Assert.False(await database.KeyExistsAsync(key).WaitAsync(TimeSpan.FromSeconds(5)));
            // A string at the exact probe key makes the provider's hash read fail (WRONGTYPE).
            await database.StringSetAsync(key, "probe-sentinel").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HealthStatus.Degraded, (await health.CheckHealthAsync(new())).Status);
            Assert.Equal("probe-sentinel", (string?)await database.StringGetAsync(key).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("unchanged", (string?)await database.StringGetAsync(unrelated).WaitAsync(TimeSpan.FromSeconds(5)));
            await database.KeyDeleteAsync(key).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
        }
        finally { await database.KeyDeleteAsync(new RedisKey[] { key, unrelated }).WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CheckHealthAsync_WhenRealProviderResponseHeld_DistinguishesBudgetAndCallerCancellation(bool cancelled)
    {
        await using var proxy = new RedisProxy(Infrastructure.Cache.RedisCacheTests.Connection());
        using var provider = new ProviderCache(Options.Create(new RedisCacheOptions { Configuration = proxy.Connection }));
        var prefix = $"service-test:{Guid.NewGuid():N}";
        var health = new RedisHealthCheck(provider, Options.Create(new CacheOptions { KeyPrefix = prefix }));
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
        var observed = proxy.PauseResponsesFor(prefix + ":readiness");
        using var caller = new CancellationTokenSource(); var timer = Stopwatch.StartNew();
        var work = health.CheckHealthAsync(new(), caller.Token);
        try
        {
            await observed.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(work.IsCompleted);
            if (cancelled)
            {
                caller.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            else
            {
                Assert.Equal(HealthStatus.Degraded, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Status);
                Assert.False(caller.IsCancellationRequested); Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(1.5));
            }
        }
        finally { proxy.Resume(); }
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
    }
    [Fact]
    public async Task GetAsync_WhenRedisConnectionRecovers_HealthAndAdapterRecover()
    {
        var upstream = Infrastructure.Cache.RedisCacheTests.Connection();
        await using var proxy = new RedisProxy(upstream);
        using var provider = new ProviderCache(Options.Create(new RedisCacheOptions { Configuration = proxy.Connection }));
        var prefix = $"service-test:{Guid.NewGuid():N}"; var key = prefix + ":recovery";
        var health = new RedisHealthCheck(provider, Options.Create(new CacheOptions { KeyPrefix = prefix }));
        var cache = new Service.Api.Infrastructure.Cache.RedisCache(provider, Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<Service.Api.Infrastructure.Cache.RedisCache>.Instance);
        using var direct = await ConnectionMultiplexer.ConnectAsync(upstream);
        try
        {
            await cache.SetAsync(key, new Payload { Name = "retained" }, TimeSpan.FromSeconds(60));
            Assert.Equal("retained", (await cache.GetAsync<Payload>(key))!.Name);
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
            proxy.Disconnect();
            Assert.Equal(HealthStatus.Degraded, (await health.CheckHealthAsync(new())).Status);
            Assert.Null(await cache.GetAsync<Payload>(key));
            proxy.Resume();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await health.CheckHealthAsync(new(), deadline.Token)).Status != HealthStatus.Healthy) await Task.Delay(50, deadline.Token);
            Assert.Equal("retained", (await cache.GetAsync<Payload>(key, deadline.Token))!.Name);
            await cache.SetAsync(key, new Payload { Name = "recovered" }, TimeSpan.FromSeconds(60), deadline.Token);
            Assert.Equal("recovered", (await cache.GetAsync<Payload>(key, deadline.Token))!.Name);
            await cache.RemoveAsync(key, deadline.Token); Assert.Null(await cache.GetAsync<Payload>(key, deadline.Token));
        }
        finally { proxy.Resume(); await direct.GetDatabase().KeyDeleteAsync(key).WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    public sealed class Payload { public string Name { get; set; } = ""; }
}
