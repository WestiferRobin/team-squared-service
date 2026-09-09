using Microsoft.AspNetCore.Hosting;
using Service.Api.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Service.Api.IntegrationTests.Controllers;

public class EndpointSurfaceTests
{
    [Fact]
    public void Production_endpoint_surface_contains_only_the_template_routes()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = "", ["ConnectionStrings:Redis"] = "" })));
        using var client = factory.CreateClient();
        Assert.Equal("Production", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var routes = endpoints.SelectMany(endpoint =>
        {
            // Health middleware accepts all methods; GET is the documented health contract.
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" };
            return methods.Select(method => $"{method} /{endpoint.RoutePattern.RawText!.TrimStart('/')}");
        }).OrderBy(route => route).ToArray();
        var expected = new[]
        {
            "GET /items", "POST /items", "GET /items/{itemId:guid}", "PUT /items/{itemId:guid}", "DELETE /items/{itemId:guid}",
            "GET /items/{itemId:guid}/actions", "POST /items/{itemId:guid}/actions",
            "GET /actions", "POST /actions", "GET /actions/{actionId:guid}", "PUT /actions/{actionId:guid}", "DELETE /actions/{actionId:guid}",
            "GET /health", "GET /ready"
        };
        Assert.Equal(expected.OrderBy(route => route), routes);
    }
}
