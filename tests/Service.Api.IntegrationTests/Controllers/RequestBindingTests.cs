using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;
namespace Service.Api.IntegrationTests.Controllers;

[Trait("Category", "Postgres")]
public class RequestBindingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetItemsAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private static readonly string[] Writes = ["create-item", "update-item", "create-action", "nested-action", "update-action"];
    public static IEnumerable<object[]> InvalidBodies() =>
        from route in Writes
        from kind in new[] { "missing-body", "empty-json-body", "null", "malformed", "array", "scalar", "wrong-name-type", "media-type" }
        select new object[] { route, kind };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task RequestBinding_WhenBodyCannotBind_ReturnsProblemWithoutMutation(string route, string kind)
    {
        var (item, action) = await Seed();
        var before = await Snapshot();
        using var client = fixture.Factory.CreateClient();
        var (method, path) = Target(route, item, action);
        using var request = new HttpRequestMessage(method, path);
        var body = Payload(route, item, "Changed");
        body["name"] = 123;
        var json = kind switch { "empty-json-body" => "", "null" => "null", "malformed" => "{", "array" => "[]", "scalar" => "42", "wrong-name-type" => body.ToJsonString(), _ => "{}" };
        if (kind != "missing-body") request.Content = new StringContent(json, Encoding.UTF8, kind == "media-type" ? "text/plain" : "application/json");
        using var response = await client.SendAsync(request);
        // Without content or Content-Type, MVC reports 415; an empty JSON body reports 400.
        await Problem(response, kind is "media-type" or "missing-body" ? 415 : 400);
        Assert.Equal(before, await Snapshot());
    }
    [Theory]
    [InlineData("GET", "/items/not-a-guid")]
    [InlineData("PUT", "/items/not-a-guid")]
    [InlineData("DELETE", "/items/not-a-guid")]
    [InlineData("GET", "/actions/not-a-guid")]
    [InlineData("PUT", "/actions/not-a-guid")]
    [InlineData("DELETE", "/actions/not-a-guid")]
    [InlineData("GET", "/items/not-a-guid/actions")]
    [InlineData("POST", "/items/not-a-guid/actions")]
    public async Task RequestBinding_WhenRouteGuidMalformed_ReturnsNotFoundWithoutMutation(string method, string path)
    {
        await Seed(); var before = await Snapshot();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { name = "Valid", type = "create", status = "active" }) };
        using var response = await client.SendAsync(request);
        await Problem(response, 404); Assert.Equal(before, await Snapshot());
    }
    [Theory]
    [InlineData("/items")]
    [InlineData("/items/{item}")]
    [InlineData("/items/{item}/actions")]
    [InlineData("/actions")]
    [InlineData("/actions/{action}")]
    public async Task RequestBinding_WhenVerbUnsupported_ReturnsMethodNotAllowedWithoutMutation(string path)
    {
        var (item, action) = await Seed(); var before = await Snapshot();
        path = path.Replace("{item}", item.ToString()).Replace("{action}", action.ToString());
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new { name = "Changed" }) };
        using var response = await client.SendAsync(request);
        await Problem(response, 405); Assert.Equal(before, await Snapshot());
    }
    public static IEnumerable<object[]> ValidNames() => from route in Writes from length in new[] { 1, 200 } select new object[] { route, length };
    [Theory]
    [MemberData(nameof(ValidNames))]
    public async Task Validation_WhenNameAtAcceptedBoundary_PersistsAndReturnsCanonicalResult(string route, int length)
    {
        var (item, action) = await Seed(); var name = new string('x', length);
        using var client = fixture.Factory.CreateClient(); var (method, path) = Target(route, item, action);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(Payload(route, item, name)) };
        using var response = await client.SendAsync(request);
        Assert.Equal(method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, body.GetProperty("name").GetString());
        var id = body.GetProperty("id").GetGuid();
        var domain = route.EndsWith("item", StringComparison.Ordinal) ? "items" : "actions";
        if (method == HttpMethod.Post)
        {
            Assert.NotNull(response.Headers.Location);
            Assert.Equal($"/{domain}/{id}", new Uri(client.BaseAddress!, response.Headers.Location).PathAndQuery);
        }
        using var scope = fixture.Factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        if (domain == "items") Assert.Equal(name, (await db.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Name);
        else
        {
            var saved = await db.Actions.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(name, saved.Name); Assert.Equal(item, saved.ItemId);
            Assert.Equal(item, body.GetProperty("itemId").GetGuid());
        }
    }
    [Theory]
    [InlineData(null)] [InlineData("")]
    public async Task Validation_WhenUpdateItemNameNullOrEmpty_RejectsWithoutMutation(string? name)
    {
        var (item, action) = await Seed(); var before = await Snapshot();
        using var client = fixture.Factory.CreateClient();
        using var response = await client.PutAsJsonAsync($"/items/{item}", Payload("update-item", item, name));
        await Problem(response, 400); Assert.Equal(before, await Snapshot());
    }
    public static IEnumerable<object[]> ValidEnums()
    {
        foreach (var value in new[] { "active", "archived", "0" }) yield return new object[] { "update-item", value, value == "0" ? "active" : value };
        foreach (var route in new[] { "create-action", "nested-action", "update-action" })
            foreach (var value in new[] { "create", "update", "delete", "0" }) yield return new object[] { route, value, value == "0" ? "create" : value };
    }
    [Theory]
    [MemberData(nameof(ValidEnums))]
    public async Task Serialization_WhenEnumStringAccepted_ReturnsCanonicalNamedString(string route, string input, string expected)
    {
        var (item, action) = await Seed(); using var client = fixture.Factory.CreateClient();
        var body = Payload(route, item, "Valid"); var property = route == "update-item" ? "status" : "type"; body[property] = input;
        var (method, path) = Target(route, item, action);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.Equal(method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty(property).GetString());
    }
    [Fact]
    public async Task Lists_WhenEmpty_ReturnLiteralJsonArrays()
    {
        using var client = fixture.Factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/items", new { name = "Parent" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var path in new[] { "/actions", $"/items/{id}/actions" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        }
    }
    private static (HttpMethod, string) Target(string route, Guid item, Guid action) => route switch
    {
        "create-item" => (HttpMethod.Post, "/items"), "update-item" => (HttpMethod.Put, $"/items/{item}"),
        "create-action" => (HttpMethod.Post, "/actions"), "nested-action" => (HttpMethod.Post, $"/items/{item}/actions"),
        "update-action" => (HttpMethod.Put, $"/actions/{action}"), _ => throw new ArgumentOutOfRangeException(nameof(route))
    };
    private static JsonObject Payload(string route, Guid item, string? name)
    {
        var body = new JsonObject { ["name"] = name };
        if (route == "update-item") body["status"] = "archived";
        if (route.Contains("action", StringComparison.Ordinal)) body["type"] = "update";
        if (route == "create-action") body["itemId"] = item.ToString();
        return body;
    }
    private async Task<(Guid Item, Guid Action)> Seed()
    {
        using var scope = fixture.Factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var item = new ItemModel { Name = "Original Item" }; var action = new ActionModel { Item = item, Name = "Original Action" };
        db.Actions.Add(action); await db.SaveChangesAsync(); return (item.Id, action.Id);
    }
    private async Task<string> Snapshot()
    {
        using var scope = fixture.Factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var items = await db.Items.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Status, x.CreatedAt, x.UpdatedAt }).ToArrayAsync();
        var actions = await db.Actions.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ItemId, x.Name, x.Type, x.CreatedAt, x.UpdatedAt }).ToArrayAsync();
        return JsonSerializer.Serialize(new { items, actions });
    }
    private static async Task Problem(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
    }
}
