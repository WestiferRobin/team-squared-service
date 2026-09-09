using Service.Api.IntegrationTests.Fixtures;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Action;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache.Action;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Cache.Action;

[Trait("Category", "Redis")]
public class ActionCacheTests
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
        var cache = host.Services.GetRequiredService<IActionCache>();
        var backing = host.Services.GetRequiredService<IDistributedCache>();
        var dto = new ActionDto
        {
            Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Cached",
            Type = ActionType.Update, CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow
        };
        var key = $"{prefix}:actions:{dto.Id:D}";
        try
        {
            Assert.Null(await cache.GetAsync(dto.Id));
            await cache.SetAsync(dto);
            var bytes = await backing.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(bytes);
            using (var document = JsonDocument.Parse(bytes))
            {
                Assert.Equal("update", document.RootElement.GetProperty("type").GetString());
                Assert.Equal(dto.Id, document.RootElement.GetProperty("id").GetGuid());
            }
            var read = await cache.GetAsync(dto.Id);
            Assert.NotNull(read);
            Assert.Equal((dto.Id, dto.ItemId, dto.Name, dto.Type, dto.CreatedAt, dto.UpdatedAt),
                (read.Id, read.ItemId, read.Name, read.Type, read.CreatedAt, read.UpdatedAt));
            await cache.RemoveAsync(dto.Id);
            Assert.Null(await backing.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(2)));
            await backing.SetAsync(key, Encoding.UTF8.GetBytes("{}"));
            Assert.Null(await cache.GetAsync(dto.Id));
            await cache.SetAsync(dto);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && await backing.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(2)) is not null)
            {
                await Task.Delay(100);
            }
            Assert.Null(await cache.GetAsync(dto.Id));
            Assert.Null(await backing.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await backing.RemoveAsync(key);
        }
    }
}
