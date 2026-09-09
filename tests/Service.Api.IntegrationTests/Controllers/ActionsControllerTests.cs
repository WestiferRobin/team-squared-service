using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Dtos.Action.Responses;
using Service.Api.Enums;
using Service.Api.Infrastructure.Database;
using Service.Api.Models;
using Service.Api.IntegrationTests.Fixtures;
using Xunit;

namespace Service.Api.IntegrationTests.Controllers;

[Trait("Category", "Postgres")]
public class ActionsControllerTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<ActionType>(JsonNamingPolicy.CamelCase, false) }
    };
    public Task InitializeAsync() => fixture.ResetItemsAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> CreateParent(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/items", new { name = "Parent" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<ActionResponse> CreateAction(HttpClient client, Guid itemId)
    {
        using var response = await client.PostAsJsonAsync("/actions", new { itemId, name = "Original", type = "create" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActionResponse>(Json))!;
    }

    private static async Task<ProblemDetails> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal((int)status, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        return problem;
    }

    [Fact]
    public async Task Both_creation_routes_persist_actions_and_return_canonical_locations()
    {
        using var client = fixture.Factory.CreateClient();
        using var parent = await client.PostAsJsonAsync("/items", new { name = "Parent" });
        Assert.Equal(HttpStatusCode.Created, parent.StatusCode);
        var itemId = (await parent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var nested = await client.PostAsJsonAsync($"/items/{itemId}/actions", new { name = "Nested", type = "create" });
        using var direct = await client.PostAsJsonAsync("/actions", new { itemId, name = "Direct", type = "delete" });
        foreach (var response in new[] { nested, direct })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var action = await response.Content.ReadFromJsonAsync<JsonElement>();
            var actionId = action.GetProperty("id").GetGuid();
            Assert.NotNull(response.Headers.Location);
            Assert.Equal($"/actions/{actionId}", new Uri(client.BaseAddress!, response.Headers.Location).PathAndQuery);
            Assert.Equal(itemId, action.GetProperty("itemId").GetGuid());
            using var fetched = await client.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        }
        var children = await client.GetFromJsonAsync<JsonElement>($"/items/{itemId}/actions");
        Assert.Equal(2, children.GetArrayLength());
        using var stillPresent = await client.GetAsync($"/items/{itemId}");
        Assert.Equal(HttpStatusCode.OK, stillPresent.StatusCode); // Type Delete is classification only.
    }

    [Fact]
    public async Task Full_CRUD_returns_scalar_contract_and_preserves_parent_and_created_timestamp()
    {
        // Fixture deliberately disables Redis; every Action path must work without it.
        using var client = fixture.Factory.CreateClient();
        Assert.Empty((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!);
        var parent = await CreateParent(client);
        using var response = await client.PostAsJsonAsync("/actions", new { itemId = parent, name = new string('a', 200), type = "delete" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<ActionResponse>(body, Json)!;
        var shape = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(new[] { "createdAt", "id", "itemId", "name", "type", "updatedAt" },
            shape.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal("delete", shape.GetProperty("type").GetString());
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(parent, created.ItemId);
        Assert.Equal(new string('a', 200), created.Name);
        Assert.Equal(DateTimeKind.Utc, created.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, created.UpdatedAt.Kind);
        Assert.InRange(created.CreatedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        var fetched = (await client.GetFromJsonAsync<ActionResponse>(response.Headers.Location, Json))!;
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.Name, fetched.Name);
        Assert.True((created.CreatedAt - fetched.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal(created.Id, Assert.Single((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!).Id);
        using var updated = await client.PutAsJsonAsync($"/actions/{created.Id}", new { name = "Changed", type = "update" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = (await updated.Content.ReadFromJsonAsync<ActionResponse>(Json))!;
        Assert.Equal(parent, changed.ItemId);
        Assert.Equal(created.Id, changed.Id);
        Assert.Equal("Changed", changed.Name);
        Assert.Equal(ActionType.Update, changed.Type);
        Assert.Equal(fetched.CreatedAt, changed.CreatedAt);
        Assert.True(changed.UpdatedAt > fetched.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, changed.UpdatedAt.Kind);
        var persisted = (await client.GetFromJsonAsync<ActionResponse>($"/actions/{created.Id}", Json))!;
        Assert.Equal(changed.Name, persisted.Name);
        Assert.Equal(changed.Type, persisted.Type);
        using var unchanged = await client.PutAsJsonAsync($"/actions/{created.Id}", new { name = "Changed", type = "update" });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.True((await unchanged.Content.ReadFromJsonAsync<ActionResponse>(Json))!.UpdatedAt > changed.UpdatedAt);
        using var deleted = await client.DeleteAsync($"/actions/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync($"/actions/{created.Id}");
        await Problem(missing, HttpStatusCode.NotFound);
        Assert.Empty((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!);
        using var stillParent = await client.GetAsync($"/items/{parent}");
        Assert.Equal(HttpStatusCode.OK, stillParent.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("nonexistent")]
    public async Task Top_level_create_requires_a_valid_existing_parent(string kind)
    {
        using var client = fixture.Factory.CreateClient();
        var body = new JsonObject { ["name"] = "Valid", ["type"] = "create" };
        if (kind != "missing") body["itemId"] = kind switch
        {
            "null" => null, "empty" => Guid.Empty.ToString(), "malformed" => "invalid", _ => Guid.NewGuid().ToString()
        };
        using var response = await client.PostAsJsonAsync("/actions", body);
        await Problem(response, kind == "nonexistent" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest);
        Assert.Empty((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!);
    }

    [Theory]
    [InlineData("missing-name")]
    [InlineData("null-name")]
    [InlineData("blank-name")]
    [InlineData("whitespace-name")]
    [InlineData("long-name")]
    [InlineData("missing-type")]
    [InlineData("null-type")]
    [InlineData("unknown-type")]
    [InlineData("numeric-type")]
    [InlineData("undefined-type")]
    public async Task Invalid_action_fields_fail_all_write_routes_without_mutation(string kind)
    {
        using var client = fixture.Factory.CreateClient();
        var parent = await CreateParent(client);
        var action = await CreateAction(client, parent);
        var before = (await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json))!;
        var body = new JsonObject { ["name"] = "Changed", ["type"] = "update" };
        switch (kind)
        {
            case "missing-name": body.Remove("name"); break;
            case "null-name": body["name"] = null; break;
            case "blank-name": body["name"] = ""; break;
            case "whitespace-name": body["name"] = " \t\n"; break;
            case "long-name": body["name"] = new string('x', 201); break;
            case "missing-type": body.Remove("type"); break;
            case "null-type": body["type"] = null; break;
            case "unknown-type": body["type"] = "unsupported"; break;
            case "numeric-type": body["type"] = 0; break;
            case "undefined-type": body["type"] = 99; break;
        }
        using var nested = await client.PostAsJsonAsync($"/items/{parent}/actions", body);
        await Problem(nested, HttpStatusCode.BadRequest);
        using var update = await client.PutAsJsonAsync($"/actions/{action.Id}", body);
        await Problem(update, HttpStatusCode.BadRequest);
        body["itemId"] = parent.ToString();
        using var direct = await client.PostAsJsonAsync("/actions", body);
        await Problem(direct, HttpStatusCode.BadRequest);
        AssertActionValues(before!, await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json));
        Assert.Single((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Missing_action_returns_404_problem_details(string method)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/actions/{Guid.NewGuid()}");
        if (method == "PUT") request.Content = JsonContent.Create(new { name = "Valid", type = "create" });
        using var response = await client.SendAsync(request);
        var problem = await Problem(response, HttpStatusCode.NotFound);
        Assert.Equal("The requested Action was not found.", problem.Detail);
    }

    [Fact]
    public async Task Nested_routes_scope_children_and_reject_missing_parents()
    {
        using var client = fixture.Factory.CreateClient();
        var parent = await CreateParent(client);
        var other = await CreateParent(client);
        Assert.Empty((await client.GetFromJsonAsync<ActionResponse[]>($"/items/{parent}/actions", Json))!);
        await CreateAction(client, other);
        using var response = await client.PostAsJsonAsync($"/items/{parent}/actions", new { name = "Nested", type = "create" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var child = (await response.Content.ReadFromJsonAsync<ActionResponse>(Json))!;
        Assert.Equal(parent, child.ItemId);
        Assert.Equal($"/actions/{child.Id}", new Uri(client.BaseAddress!, response.Headers.Location!).PathAndQuery);
        Assert.Equal(child.Id, Assert.Single((await client.GetFromJsonAsync<ActionResponse[]>($"/items/{parent}/actions", Json))!).Id);
        Assert.Equal(child.Id, (await client.GetFromJsonAsync<ActionResponse>($"/actions/{child.Id}", Json))!.Id);
        var missingId = Guid.NewGuid();
        using var missingList = await client.GetAsync($"/items/{missingId}/actions");
        using var missingPost = await client.PostAsJsonAsync($"/items/{missingId}/actions", new { name = "Valid", type = "create" });
        Assert.Equal("The requested Item was not found.", (await Problem(missingList, HttpStatusCode.NotFound)).Detail);
        Assert.Equal("The requested Item was not found.", (await Problem(missingPost, HttpStatusCode.NotFound)).Detail);
        using var deleted = await client.DeleteAsync($"/items/{parent}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var removedChild = await client.GetAsync($"/actions/{child.Id}");
        await Problem(removedChild, HttpStatusCode.NotFound);
        Assert.Equal(other, Assert.Single((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!).ItemId);
    }

    [Fact]
    public async Task Nested_create_and_update_reject_body_ownership_including_matching_parent()
    {
        using var client = fixture.Factory.CreateClient();
        var parent = await CreateParent(client);
        var other = await CreateParent(client);
        var action = await CreateAction(client, parent);
        var before = await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json);
        foreach (var itemId in new[] { parent, other })
        {
            using var nested = await client.PostAsJsonAsync($"/items/{parent}/actions", new { itemId, name = "Conflict", type = "update" });
            using var update = await client.PutAsJsonAsync($"/actions/{action.Id}", new { itemId, name = "Conflict", type = "update" });
            await Problem(nested, HttpStatusCode.BadRequest);
            await Problem(update, HttpStatusCode.BadRequest);
        }
        AssertActionValues(before!, await client.GetFromJsonAsync<ActionResponse>($"/actions/{action.Id}", Json));
        Assert.Single((await client.GetFromJsonAsync<ActionResponse[]>("/actions", Json))!);
    }

    [Fact]
    public async Task Lists_order_by_created_time_then_id()
    {
        var parent = new ItemModel { Name = "Parent" };
        var ids = new[] { Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002") };
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            foreach (var id in ids.Reverse()) db.Actions.Add(new ActionModel { Id = id, Item = parent, Name = "Duplicate" });
            await db.SaveChangesAsync();
            var time = DateTime.UtcNow.AddDays(-1);
            await db.Actions.ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAt, time));
            await db.Actions.Where(a => a.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAt, time.AddDays(-1)));
        }
        using var client = fixture.Factory.CreateClient();
        foreach (var path in new[] { "/actions", $"/items/{parent.Id}/actions" })
            Assert.Equal(ids, (await client.GetFromJsonAsync<ActionResponse[]>(path, Json))!.Select(a => a.Id).ToArray());
    }

    private static void AssertActionValues(ActionResponse expected, ActionResponse? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Id, expected.ItemId, expected.Name, expected.Type, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.ItemId, actual.Name, actual.Type, actual.CreatedAt, actual.UpdatedAt));
    }
}
