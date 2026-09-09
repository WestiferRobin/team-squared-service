using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Responses;
using Service.Api.Dtos.Item.Responses;
using Service.Api.Enums;
using Service.Api.Infrastructure.Cache.Action;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.IntegrationTests.Infrastructure.Cache;
using Xunit;

namespace Service.Api.IntegrationTests.Controllers;

[Trait("Category", "Postgres")]
[Trait("Category", "Redis")]
public class ActionCacheTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter<ItemStatus>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<ActionType>(JsonNamingPolicy.CamelCase, false)
        }
    };

    public Task InitializeAsync() => fixture.ResetItemsAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> Host(string connection) => fixture.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = connection,
            ["Cache:DefaultTtlSeconds"] = "30"
        })));

    private static async Task<ActionResponse> CreateAsync(HttpClient client)
    {
        using var parentResponse = await client.PostAsJsonAsync("/items", new { name = "Parent" });
        Assert.Equal(HttpStatusCode.Created, parentResponse.StatusCode);
        var parent = (await parentResponse.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        using var response = await client.PostAsJsonAsync($"/items/{parent.Id}/actions", new { name = "Original", type = "create" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActionResponse>(Json))!;
    }

    [Fact]
    public async Task Reads_fill_and_hit_cache_lists_bypass_it_and_successful_writes_invalidate()
    {
        using var host = Host(RedisCacheTests.Connection());
        using var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<IActionCache>();
        var action = await CreateAsync(client);
        try
        {
            Assert.Null(await cache.GetAsync(action.Id));
            var read = (await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json))!;
            AssertValues(read, await cache.GetAsync(action.Id));
            using (var scope = host.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Actions
                    .Where(value => value.Id == action.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Name, "DB-only"));
            }
            Assert.Equal("Original", (await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json))!.Name);
            foreach (var path in new[] { "/actions", $"/items/{action.ItemId}/actions" })
            {
                Assert.Equal("DB-only", Assert.Single((await client.GetFromJsonAsync<ActionResponse[]>(path, Json))!).Name);
            }
            using var updated = await client.PutAsJsonAsync($"/actions/{action.Id}", new { name = "Updated", type = "update" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Null(await cache.GetAsync(action.Id));
            var changed = (await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json))!;
            Assert.Equal("Updated", changed.Name);
            AssertValues(changed, await cache.GetAsync(action.Id));
            using var deleted = await client.DeleteAsync($"/actions/{action.Id}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Null(await cache.GetAsync(action.Id));
            using var missing = await client.GetAsync($"/actions/{action.Id}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { await cache.RemoveAsync(action.Id); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleted_database_row_cannot_be_returned_from_stale_cache(bool cascade)
    {
        using var host = Host(RedisCacheTests.Connection());
        using var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<IActionCache>();
        var action = await CreateAsync(client);
        try
        {
            using var populated = await client.GetAsync($"/actions/{action.Id}");
            Assert.Equal(HttpStatusCode.OK, populated.StatusCode);
            Assert.NotNull(await cache.GetAsync(action.Id));
            if (cascade)
            {
                using var deleted = await client.DeleteAsync($"/items/{action.ItemId}");
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }
            else
            {
                // Simulate a committed deletion whose cache invalidation did not occur.
                using var scope = host.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Actions
                    .Where(value => value.Id == action.Id).ExecuteDeleteAsync();
            }
            Assert.NotNull(await cache.GetAsync(action.Id));
            using var missing = await client.GetAsync($"/actions/{action.Id}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
            Assert.Null(await cache.GetAsync(action.Id));
        }
        finally { await cache.RemoveAsync(action.Id); }
    }

    [Fact]
    public async Task Failed_update_preserves_persisted_and_cached_values()
    {
        using var host = Host(RedisCacheTests.Connection());
        using var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<IActionCache>();
        var action = await CreateAsync(client);
        using var populated = await client.GetAsync($"/actions/{action.Id}");
        Assert.Equal(HttpStatusCode.OK, populated.StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Actions\" ADD CONSTRAINT test_reject_update CHECK (\"Name\" <> 'Reject')");
        try
        {
            using var failed = await client.PutAsJsonAsync($"/actions/{action.Id}", new { name = "Reject", type = "update" });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal("Original", (await cache.GetAsync(action.Id))!.Name);
            Assert.Equal("Original", (await db.Actions.AsNoTracking().SingleAsync(value => value.Id == action.Id)).Name);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Actions\" DROP CONSTRAINT test_reject_update");
            await cache.RemoveAsync(action.Id);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:1,connectTimeout=100,asyncTimeout=100,connectRetry=0,abortConnect=true")]
    public async Task Redis_outage_preserves_action_CRUD(string connection)
    {
        using var host = Host(connection);
        using var client = host.CreateClient();
        var action = await CreateAsync(client);
        Assert.Equal("Original", (await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json))!.Name);
        using var updated = await client.PutAsJsonAsync($"/actions/{action.Id}", new { name = "Fallback", type = "update" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Fallback", (await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json))!.Name);
        using var deleted = await client.DeleteAsync($"/actions/{action.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync($"/actions/{action.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static void AssertValues(ActionResponse expected, ActionDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Id, expected.ItemId, expected.Name, expected.Type, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.ItemId, actual.Name, actual.Type, actual.CreatedAt, actual.UpdatedAt));
    }
}
