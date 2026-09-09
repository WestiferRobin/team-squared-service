using Service.Api.Dtos.Item;
using Microsoft.Extensions.Caching.Distributed;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Item.Responses;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.IntegrationTests.Infrastructure.Cache;
using Xunit;

namespace Service.Api.IntegrationTests.Controllers;

[Trait("Category", "Postgres")]
[Trait("Category", "Redis")]
public class ItemCacheTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<ItemStatus>(JsonNamingPolicy.CamelCase, false) }
    };
    public Task InitializeAsync() => fixture.ResetItemsAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private WebApplicationFactory<Program> Host(string connection) => fixture.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = connection,
            ["Cache:DefaultTtlSeconds"] = "30"
        })));

    [Fact]
    public async Task Reads_populate_cache_and_mutations_invalidate_after_persistence()
    {
        using var host = Host(RedisCacheTests.Connection());
        using var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<ICache>();
        using var created = await client.PostAsJsonAsync("/items", new { name = "Original" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        var key = $"{fixture.CachePrefix}:items:{item.Id}";
        try
        {
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            var read = await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json);
            AssertItemValues(read!, await cache.GetAsync<ItemDto>(key));
            var bytes = await host.Services.GetRequiredService<IDistributedCache>().GetAsync(key);
            Assert.NotNull(bytes);
            using var payload = JsonDocument.Parse(bytes);
            Assert.Equal(new[] { "createdAt", "id", "name", "status", "updatedAt" },
                payload.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
            // A direct database change proves the subsequent GET is served by Redis.
            using (var scope = host.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Items
                    .Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Name, "DB-only"));
            Assert.Equal("Original", (await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json))!.Name);
            Assert.Equal("DB-only", Assert.Single((await client.GetFromJsonAsync<ItemResponse[]>("/items", Json))!).Name);
            using var updated = await client.PutAsJsonAsync($"/items/{item.Id}", new { name = "Updated", status = "archived" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            Assert.Equal("Updated", (await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json))!.Name);
            Assert.Equal("Updated", (await cache.GetAsync<ItemDto>(key))!.Name);
            using var deleted = await client.DeleteAsync($"/items/{item.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            using var missing = await client.GetAsync($"/items/{item.Id}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Null(await cache.GetAsync<ItemDto>(key));
            using var ready = await client.GetAsync("/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        }
        finally { await cache.RemoveAsync(key); }
    }

    [Fact]
    public async Task Failed_database_update_does_not_invalidate_the_cache()
    {
        using var host = Host(RedisCacheTests.Connection());
        using var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<ICache>();
        using var created = await client.PostAsJsonAsync("/items", new { name = "Keep" });
        var item = (await created.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        var key = $"{fixture.CachePrefix}:items:{item.Id}";
        using var populated = await client.GetAsync($"/items/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, populated.StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" ADD CONSTRAINT test_reject_update CHECK (\"Name\" <> 'Reject')");
        try
        {
            using var failed = await client.PutAsJsonAsync($"/items/{item.Id}", new { name = "Reject", status = "active" });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal("Keep", (await cache.GetAsync<ItemDto>(key))!.Name);
            Assert.Equal("Keep", (await db.Items.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Name);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" DROP CONSTRAINT test_reject_update");
            await cache.RemoveAsync(key);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:1,connectTimeout=100,asyncTimeout=100,connectRetry=0,abortConnect=true")]
    public async Task Missing_or_unreachable_Redis_preserves_CRUD_and_degrades_readiness(string connection)
    {
        using var host = Host(connection);
        using var client = host.CreateClient();
        using var created = await client.PostAsJsonAsync("/items", new { name = "Fallback" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        Assert.Equal("Fallback", (await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json))!.Name);
        using var updated = await client.PutAsJsonAsync($"/items/{item.Id}", new { name = "Committed", status = "active" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Committed", (await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json))!.Name);
        using var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", await ready.Content.ReadAsStringAsync());
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var deleted = await client.DeleteAsync($"/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync($"/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static void AssertItemValues(ItemResponse expected, ItemDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Id, expected.Name, expected.Status, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.Name, actual.Status, actual.CreatedAt, actual.UpdatedAt));
    }
}
