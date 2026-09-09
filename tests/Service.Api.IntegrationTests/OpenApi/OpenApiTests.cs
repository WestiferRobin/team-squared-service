using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Service.Api.IntegrationTests.OpenApi;

public class OpenApiTests
{
    private static WebApplicationFactory<Program> Host(string environment, bool? enabled) =>
        new Service.Api.IntegrationTests.Fixtures.ConfigurationApiFactory(environment,
            enabled.HasValue ? new Dictionary<string, string?> { ["OpenApi:Enabled"] = enabled.Value.ToString() } : null);

    [Theory]
    [InlineData("Testing", null)]
    [InlineData("Production", null)]
    [InlineData("Staging", null)]
    [InlineData("Development", false)]
    public async Task Disabled_documentation_is_unavailable(string environment, bool? enabled)
    {
        using var factory = Host(environment, enabled);
        using var client = factory.CreateClient();
        Assert.Equal(environment, factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.False(factory.Services.GetRequiredService<IConfiguration>().GetValue<bool>("OpenApi:Enabled"));
        foreach (var path in new[] { "/swagger/v1/swagger.json", "/swagger", "/swagger/index.html" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Staging", true)]
    [InlineData("Testing", true)]
    public async Task Enabled_documentation_exposes_ui_and_document(string environment, bool? enabled)
    {
        using var factory = Host(environment, enabled);
        using var client = factory.CreateClient();
        Assert.Equal(environment, factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.True(factory.Services.GetRequiredService<IConfiguration>().GetValue<bool>("OpenApi:Enabled"));
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync());
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Document_DescribesCompleteHttpContract()
    {
        using var factory = Host("Testing", true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
        var paths = root.GetProperty("paths");
        Assert.Equal(new[] { "/actions", "/actions/{actionId}", "/items", "/items/{itemId}", "/items/{itemId}/actions" }, paths.EnumerateObject().Select(path => path.Name).OrderBy(path => path, StringComparer.Ordinal));
        foreach (var (path, methods) in new[]
        {
            ("/items", new[] { "get", "post" }),
            ("/items/{itemId}", new[] { "get", "put", "delete" }),
            ("/items/{itemId}/actions", new[] { "get", "post" }),
            ("/actions", new[] { "get", "post" }),
            ("/actions/{actionId}", new[] { "get", "put", "delete" })
        })
        {
            Assert.Equal(methods.OrderBy(method => method), paths.GetProperty(path).EnumerateObject().Select(operation => operation.Name).OrderBy(method => method));
        }
        foreach (var (path, result, list) in new[]
        {
            ("/items", "ItemResponse", true), ("/items/{itemId}", "ItemResponse", false),
            ("/items/{itemId}/actions", "ActionResponse", true), ("/actions", "ActionResponse", true), ("/actions/{actionId}", "ActionResponse", false)
        })
        {
            var schema = paths.GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            if (list) { Assert.Equal("array", schema.GetProperty("type").GetString()); schema = schema.GetProperty("items"); }
            Assert.Equal($"#/components/schemas/{result}", schema.GetProperty("$ref").GetString());
        }
        foreach (var (path, request, result) in new[] { ("/items/{itemId}", "UpdateItemRequest", "ItemResponse"), ("/actions/{actionId}", "UpdateActionRequest", "ActionResponse") })
        {
            var put = paths.GetProperty(path).GetProperty("put");
            Assert.Equal($"#/components/schemas/{request}", put.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Equal($"#/components/schemas/{result}", put.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }

        foreach (var (path, request, result) in new[]
        {
            ("/items", "CreateItemRequest", "ItemResponse"),
            ("/items/{itemId}/actions", "CreateItemActionRequest", "ActionResponse"),
            ("/actions", "CreateActionRequest", "ActionResponse")
        })
        {
            var post = paths.GetProperty(path).GetProperty("post");
            Assert.Equal($"#/components/schemas/{request}", post.GetProperty("requestBody")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Equal($"#/components/schemas/{result}", post.GetProperty("responses").GetProperty("201")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }
        foreach (var path in new[] { "/items/{itemId}", "/actions/{actionId}" })
            Assert.False(paths.GetProperty(path).GetProperty("delete").GetProperty("responses")
                .GetProperty("204").TryGetProperty("content", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        foreach (var (name, values) in new[]
        {
            ("ItemStatus", new[] { "active", "archived" }),
            ("ActionType", new[] { "create", "update", "delete" })
        })
        {
            var schema = schemas.GetProperty(name);
            Assert.Equal("string", schema.GetProperty("type").GetString());
            Assert.Equal(values, schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        }
        foreach (var name in new[] { "ItemResponse", "ActionResponse" })
        {
            var properties = schemas.GetProperty(name).GetProperty("properties");
            Assert.Equal("uuid", properties.GetProperty("id").GetProperty("format").GetString());
            Assert.True(properties.TryGetProperty("createdAt", out _));
            Assert.True(properties.TryGetProperty("updatedAt", out _));
        }
        foreach (var (name, fields) in new[]
        {
            ("CreateItemRequest", new[] { "name" }), ("UpdateItemRequest", new[] { "name", "status" }),
            ("CreateActionRequest", new[] { "itemId", "name", "type" }), ("CreateItemActionRequest", new[] { "name", "type" }), ("UpdateActionRequest", new[] { "name", "type" }),
            ("ItemResponse", new[] { "id", "name", "status", "createdAt", "updatedAt" }), ("ActionResponse", new[] { "id", "itemId", "name", "type", "createdAt", "updatedAt" })
        })
        {
            var schema = schemas.GetProperty(name);
            Assert.Equal(fields.OrderBy(field => field), schema.GetProperty("properties").EnumerateObject().Select(field => field.Name).OrderBy(field => field));
            if (name.EndsWith("Request", StringComparison.Ordinal))
            {
                Assert.Equal(fields.OrderBy(field => field), schema.GetProperty("required").EnumerateArray().Select(field => field.GetString()).OrderBy(field => field));
                Assert.Equal(200, schema.GetProperty("properties").GetProperty("name").GetProperty("maxLength").GetInt32());
            }
        }
        foreach (var name in new[] { "CreateActionRequest", "CreateItemActionRequest", "UpdateActionRequest", "ActionResponse" })
            Assert.Equal("#/components/schemas/ActionType", schemas.GetProperty(name).GetProperty("properties").GetProperty("type").GetProperty("$ref").GetString());
        Assert.Equal(200, schemas.GetProperty("CreateItemRequest").GetProperty("properties")
            .GetProperty("name").GetProperty("maxLength").GetInt32());
        Assert.Contains("itemId", schemas.GetProperty("CreateActionRequest").GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.False(schemas.GetProperty("CreateItemActionRequest").GetProperty("properties").TryGetProperty("itemId", out _));
        Assert.False(schemas.GetProperty("UpdateActionRequest").GetProperty("properties").TryGetProperty("itemId", out _));
        Assert.Equal("#/components/schemas/ItemStatus", schemas.GetProperty("UpdateItemRequest")
            .GetProperty("properties").GetProperty("status").GetProperty("$ref").GetString());
    }
}
