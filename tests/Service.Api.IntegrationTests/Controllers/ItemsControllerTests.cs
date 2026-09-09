using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Item.Responses;
using Service.Api.Enums;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;

namespace Service.Api.IntegrationTests.Controllers;

[Trait("Category", "Postgres")]
public class ItemsControllerTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<ItemStatus>(JsonNamingPolicy.CamelCase, false) }
    };

    public Task InitializeAsync() => fixture.ResetItemsAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Full_CRUD_flow_persists_and_returns_the_DTO_contract()
    {
        using var client = fixture.Factory.CreateClient();
        var before = DateTime.UtcNow.AddSeconds(-1);
        using var created = await client.PostAsJsonAsync("/items", new { name = new string('a', 200) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        var item = (await created.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal(new string('a', 200), item.Name);
        Assert.Equal(ItemStatus.Active, item.Status);
        Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, item.UpdatedAt.Kind);
        Assert.InRange(item.CreatedAt, before, DateTime.UtcNow);
        Assert.True(item.UpdatedAt >= item.CreatedAt);
        Assert.NotNull(created.Headers.Location);
        Assert.EndsWith($"/items/{item.Id}", created.Headers.Location.ToString());
        using var shape = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "createdAt", "id", "name", "status", "updatedAt" },
            shape.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal("active", shape.RootElement.GetProperty("status").GetString());

        using var fetched = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var stored = (await fetched.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        Assert.Equal(item.Id, stored.Id);
        Assert.Equal(item.Name, stored.Name);
        Assert.True((stored.CreatedAt - item.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1));
        var list = (await client.GetFromJsonAsync<ItemResponse[]>("/items", Json))!;
        Assert.Equal(item.Id, Assert.Single(list).Id);

        using var updated = await client.PutAsJsonAsync($"/items/{item.Id}",
            new { name = "Archived example", status = "archived", id = Guid.NewGuid(), createdAt = before.AddYears(-1) });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = (await updated.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        Assert.Equal(item.Id, changed.Id);
        Assert.Equal("Archived example", changed.Name);
        Assert.Equal(ItemStatus.Archived, changed.Status);
        Assert.True((changed.CreatedAt - stored.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal(DateTimeKind.Utc, changed.UpdatedAt.Kind);
        Assert.True(changed.UpdatedAt > stored.UpdatedAt);
        Assert.InRange(changed.UpdatedAt, before, DateTime.UtcNow);
        var persisted = (await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json))!;
        Assert.Equal(changed.Name, persisted.Name);
        Assert.Equal(changed.Status, persisted.Status);

        using var unchanged = await client.PutAsJsonAsync($"/items/{item.Id}", new { name = changed.Name, status = "archived" });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        var refreshed = (await unchanged.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        Assert.True(refreshed.UpdatedAt > changed.UpdatedAt);

        using var deleted = await client.DeleteAsync($"/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync($"/items/{item.Id}");
        await AssertProblem(missing, HttpStatusCode.NotFound);
        Assert.Empty((await client.GetFromJsonAsync<ItemResponse[]>("/items", Json))!);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Items.AnyAsync());
    }

    [Fact]
    public async Task Empty_list_returns_an_empty_JSON_array()
    {
        using var client = fixture.Factory.CreateClient();
        using var response = await client.GetAsync("/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_orders_by_creation_time_then_id()
    {
        var earlier = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var firstTie = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondTie = Guid.Parse("00000000-0000-0000-0000-000000000002");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            db.Items.AddRange(new ItemModel { Id = secondTie, Name = "Second" },
                new ItemModel { Id = earlier, Name = "Earlier" }, new ItemModel { Id = firstTie, Name = "First" });
            await db.SaveChangesAsync();
            var timestamp = DateTime.UtcNow.AddDays(-1);
            await db.Items.ExecuteUpdateAsync(setters => setters.SetProperty(i => i.CreatedAt, timestamp));
            await db.Items.Where(i => i.Id == earlier).ExecuteUpdateAsync(setters => setters.SetProperty(i => i.CreatedAt, timestamp.AddDays(-1)));
        }
        using var client = fixture.Factory.CreateClient();
        var items = (await client.GetFromJsonAsync<ItemResponse[]>("/items", Json))!;
        Assert.Equal(new[] { earlier, firstTie, secondTie }, items.Select(i => i.Id).ToArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"name\":\"\\t\\n\"}")]
    [InlineData("too-long")]
    public async Task Invalid_create_returns_400_without_persisting(string json)
    {
        if (json == "too-long") json = JsonSerializer.Serialize(new { name = new string('x', 201) });
        using var client = fixture.Factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/items", content);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Empty((await client.GetFromJsonAsync<ItemResponse[]>("/items", Json))!);
    }

    [Theory]
    [InlineData("{\"name\":\"Changed\",\"status\":\"unknown\"}")]
    [InlineData("{\"name\":\"Changed\",\"status\":99}")]
    [InlineData("{\"name\":\"Changed\",\"status\":0}")]
    [InlineData("{\"name\":\"Changed\"}")]
    [InlineData("{\"status\":\"active\"}")]
    [InlineData("{\"name\":\"   \",\"status\":\"active\"}")]
    [InlineData("too-long")]
    public async Task Invalid_update_returns_400_without_changing_the_item(string json)
    {
        using var client = fixture.Factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/items", new { name = "Original" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<ItemResponse>(Json))!;
        if (json == "too-long") json = JsonSerializer.Serialize(new { name = new string('x', 201), status = "active" });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"/items/{item.Id}", content);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        var stored = (await client.GetFromJsonAsync<ItemResponse>($"/items/{item.Id}", Json))!;
        Assert.Equal("Original", stored.Name);
        Assert.Equal(ItemStatus.Active, stored.Status);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Missing_item_returns_404_problem_details(string method)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/items/{Guid.NewGuid()}");
        if (method == "PUT") request.Content = JsonContent.Create(new { name = "Valid", status = "active" });
        using var response = await client.SendAsync(request);
        var problem = await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal("The requested Item was not found.", problem.Detail);
    }

    private static async Task<ProblemDetails> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal((int)status, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        return problem;
    }
}
