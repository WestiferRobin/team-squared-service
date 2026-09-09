using Service.Api.IntegrationTests.Fixtures;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Item;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache.Item;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Cache.Item;

[Trait("Category", "Redis")]
public class ItemCacheTests
{
    [Fact]
    public async Task Domain_key_payload_removal_and_configured_expiration_round_trip()
    {
        var prefix = $"service-test:{Guid.NewGuid():N}";
        using var host = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = RedisCacheTests.Connection(),
                    ["Cache:KeyPrefix"] = prefix,
                    ["Cache:DefaultTtlSeconds"] = "1"
                })));
        var cache = host.Services.GetRequiredService<IItemCache>();
        var backing = host.Services.GetRequiredService<IDistributedCache>();
        var dto = new ItemDto
        {
            Id = Guid.NewGuid(), Name = "Cached",
            Status = ItemStatus.Archived, CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow
        };
        var key = $"{prefix}:items:{dto.Id:D}";
        try
        {
            Assert.Null(await cache.GetAsync(dto.Id));
            await cache.SetAsync(dto);
            var bytes = await backing.GetAsync(key);
            Assert.NotNull(bytes);
            using (var document = JsonDocument.Parse(bytes))
            {
                Assert.Equal("archived", document.RootElement.GetProperty("status").GetString());
                Assert.Equal(dto.Id, document.RootElement.GetProperty("id").GetGuid());
            }
            var read = await cache.GetAsync(dto.Id);
            Assert.NotNull(read);
            Assert.Equal((dto.Id, dto.Name, dto.Status, dto.CreatedAt, dto.UpdatedAt),
                (read.Id, read.Name, read.Status, read.CreatedAt, read.UpdatedAt));
            await cache.RemoveAsync(dto.Id);
            Assert.Null(await backing.GetAsync(key));
            await backing.SetAsync(key, Encoding.UTF8.GetBytes("{}"));
            Assert.Null(await cache.GetAsync(dto.Id));
            await cache.SetAsync(dto);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && await backing.GetAsync(key) is not null)
            {
                await Task.Delay(100);
            }
            Assert.Null(await cache.GetAsync(dto.Id));
            Assert.Null(await backing.GetAsync(key));
        }
        finally
        {
            await backing.RemoveAsync(key);
        }
    }
}
