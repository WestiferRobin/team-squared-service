using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Service.Api.IntegrationTests.Controllers;

public class OpenApiTests
{
    private static WebApplicationFactory<Program> Host(string environment, bool? enabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "",
                    ["ConnectionStrings:Redis"] = ""
                };
                if (enabled.HasValue)
                    values["OpenApi:Enabled"] = enabled.Value.ToString();
                config.AddInMemoryCollection(values);
            });
        });

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
    public async Task Enabled_documentation_exposes_ui_and_meaningful_contract(string environment, bool? enabled)
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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
        var paths = root.GetProperty("paths");
        foreach (var (path, methods) in new[]
        {
            ("/items", new[] { "get", "post" }),
            ("/items/{itemId}", new[] { "get", "put", "delete" }),
            ("/items/{itemId}/actions", new[] { "get", "post" }),
            ("/actions", new[] { "get", "post" }),
            ("/actions/{actionId}", new[] { "get", "put", "delete" })
        })
            foreach (var method in methods)
                Assert.True(paths.GetProperty(path).TryGetProperty(method, out _));

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
