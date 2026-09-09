using Service.Api.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Item;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Cache;

[Trait("Category", "Redis")]
public class RedisCacheTests
{
    internal static string Connection()
    {
        var value = Environment.GetEnvironmentVariable("ConnectionStrings__Redis");
        return !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidOperationException("Set ConnectionStrings__Redis to the Compose Redis endpoint.");
    }

    [Fact]
    public async Task Real_Redis_round_trip_miss_removal_and_expiration()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = Connection() })));
        var cache = factory.Services.GetRequiredService<ICache>();
        var backing = factory.Services.GetRequiredService<IDistributedCache>();
        var key = $"service-test:{Guid.NewGuid()}:roundtrip";
        var now = DateTime.UtcNow;
        var dto = new ItemDto { Id = Guid.NewGuid(), Name = "Redis example", Status = ItemStatus.Archived, CreatedAt = now, UpdatedAt = now };
        try
        {
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            Assert.Null(await backing.GetAsync(key));
            await cache.SetAsync(key, dto, TimeSpan.FromSeconds(30));
            AssertItemValues(dto, await cache.GetAsync<ItemDto>(key));
            await cache.RemoveAsync(key);
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            Assert.Null(await backing.GetAsync(key));
            await cache.SetAsync(key, dto, TimeSpan.FromSeconds(1));
            Assert.NotNull(await cache.GetAsync<ItemDto>(key));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && await backing.GetAsync(key) is not null)
                await Task.Delay(100);
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            Assert.Null(await backing.GetAsync(key));
        }
        finally { await cache.RemoveAsync(key); }
    }

    private static void AssertItemValues(ItemDto expected, ItemDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Id, expected.Name, expected.Status, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.Name, actual.Status, actual.CreatedAt, actual.UpdatedAt));
    }
}
