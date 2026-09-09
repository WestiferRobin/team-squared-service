using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Item;
using Service.Api.Dtos.Action;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache.Item;
using Service.Api.Infrastructure.Cache.Action;
using Service.Api.IntegrationTests.Fixtures;
using StackExchange.Redis;
using Xunit;
namespace Service.Api.IntegrationTests.Infrastructure.Cache;
[Trait("Category", "Redis")]
public class DomainCacheIsolationTests
{
    [Fact]
    public async Task RemoveAsync_WhenDomainsSharePrefixAndId_PreservesOtherDomain()
    {
        var connection = RedisCacheTests.Connection(); var prefix = $"service-test:{Guid.NewGuid():N}"; var id = Guid.NewGuid();
        using var factory = new ConfigurationApiFactory(overrides: new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = connection, ["Cache:KeyPrefix"] = prefix });
        var items = factory.Services.GetRequiredService<IItemCache>(); var actions = factory.Services.GetRequiredService<IActionCache>();
        var item = new ItemDto { Id = id, Name = "Item", Status = ItemStatus.Archived };
        var action = new ActionDto { Id = id, ItemId = Guid.NewGuid(), Name = "Action", Type = ActionType.Delete };
        using var redis = await ConnectionMultiplexer.ConnectAsync(connection); var db = redis.GetDatabase();
        var itemKey = $"{prefix}:items:{id:D}"; var actionKey = $"{prefix}:actions:{id:D}";
        try
        {
            await items.SetAsync(item); await actions.SetAsync(action);
            Assert.Equal(item.Name, (await items.GetAsync(id))!.Name); Assert.Equal(action.Name, (await actions.GetAsync(id))!.Name);
            Assert.True(await db.KeyExistsAsync(itemKey).WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(await db.KeyExistsAsync(actionKey).WaitAsync(TimeSpan.FromSeconds(5)));
            await items.RemoveAsync(id); Assert.Null(await items.GetAsync(id)); Assert.NotNull(await actions.GetAsync(id));
            await items.SetAsync(item); await actions.RemoveAsync(id); Assert.Null(await actions.GetAsync(id)); Assert.NotNull(await items.GetAsync(id));
        }
        finally { await db.KeyDeleteAsync(new RedisKey[] { itemKey, actionKey }).WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
