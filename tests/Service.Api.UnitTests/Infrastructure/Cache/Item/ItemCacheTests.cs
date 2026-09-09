using Microsoft.Extensions.Options;
using Service.Api.Dtos.Item;
using Service.Api.Infrastructure.Cache;
using Service.Api.Infrastructure.Cache.Item;
using Xunit;
namespace Service.Api.UnitTests.Infrastructure.Cache.Item;
public class ItemCacheTests
{
    [Theory]
    [InlineData("get", false)] [InlineData("set", false)] [InlineData("remove", false)]
    [InlineData("get", true)] [InlineData("set", true)] [InlineData("remove", true)]
    public async Task Operation_WhenInvoked_ForwardsExactContractAndFailures(string operation, bool fail)
    {
        using var caller = new CancellationTokenSource();
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var dto = new ItemDto { Id = id, Name = "Cached" };
        var failure = new InvalidOperationException("Controlled failure");
        var backing = new RecordingCache { Result = dto, Failure = fail ? failure : null };
        var cache = new ItemCache(backing, Options.Create(new CacheOptions { KeyPrefix = "custom:prefix", DefaultTtlSeconds = 123 }));
        async Task Run()
        {
            if (operation == "get") Assert.Same(dto, await cache.GetAsync(id, caller.Token));
            else if (operation == "set") await cache.SetAsync(dto, caller.Token);
            else await cache.RemoveAsync(id, caller.Token);
        }
        if (fail) Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(Run));
        else await Run();
        var call = Assert.Single(backing.Calls);
        Assert.Equal(operation, call.Operation);
        Assert.Equal("custom:prefix:items:00112233-4455-6677-8899-aabbccddeeff", call.Key);
        Assert.Equal(caller.Token, call.Token);
        if (operation != "remove") Assert.Equal(typeof(ItemDto), call.Type);
        if (operation == "set") { Assert.Same(dto, call.Value); Assert.Equal(TimeSpan.FromSeconds(123), call.Ttl); }
    }
    [Fact]
    public async Task GetAsync_WhenBackingMisses_ReturnsNull()
    {
        var cache = new ItemCache(new RecordingCache(), Options.Create(new CacheOptions()));
        Assert.Null(await cache.GetAsync(Guid.NewGuid()));
    }
}
