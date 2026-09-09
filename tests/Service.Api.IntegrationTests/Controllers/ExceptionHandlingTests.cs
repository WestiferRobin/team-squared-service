using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Service.Api.IntegrationTests.Fixtures;
using Xunit;
using Microsoft.Extensions.Logging;

namespace Service.Api.IntegrationTests.Controllers;

public class ExceptionHandlingTests
{
    [Fact]
    public async Task Unexpected_exception_logging_omits_potential_secrets()
    {
        using var logs = new RecordingLoggerProvider();
        using var factory = new ErrorApiFactory("Production").WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/__tests/errors/unexpected");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var entry = Assert.Single(logs.Messages);
        Assert.Equal("Service.Api.Exceptions.ApiExceptionHandler", entry.Category);
        Assert.Equal(LogLevel.Error, entry.Level);
        var message = entry.Message;
        Assert.Contains("InvalidOperationException", message);
        Assert.DoesNotContain("INTERNAL_SECRET_SENTINEL", message);
    }

    [Theory]
    [InlineData("Production", "item-not-found", 404, "Not Found", "The requested Item was not found.")]
    [InlineData("Production", "action-not-found", 404, "Not Found", "The requested Action was not found.")]
    [InlineData("Development", "item-not-found", 404, "Not Found", "The requested Item was not found.")]
    [InlineData("Development", "action-not-found", 404, "Not Found", "The requested Action was not found.")]
    [InlineData("Production", "not-found", 404, "Not Found", "The requested resource was not found.")]
    [InlineData("Production", "validation", 400, "Bad Request", "The request is invalid.")]
    [InlineData("Production", "unexpected", 500, "Internal Server Error", "An unexpected error occurred.")]
    [InlineData("Development", "not-found", 404, "Not Found", "The requested resource was not found.")]
    [InlineData("Development", "validation", 400, "Bad Request", "The request is invalid.")]
    [InlineData("Development", "unexpected", 500, "Internal Server Error", "An unexpected error occurred.")]
    public async Task Exceptions_return_safe_problem_details(
        string environment, string kind, int status, string title, string detail)
    {
        using var logs = new RecordingLoggerProvider();
        using var factory = new ErrorApiFactory(environment).WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        Assert.Equal(environment, factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        using var response = await client.GetAsync($"/__tests/errors/{kind}");

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal(detail, problem.Detail);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("INTERNAL_SECRET_SENTINEL", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", body);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", body);
        if (status < 500) Assert.Empty(logs.Messages);
        else
        {
            var entry = Assert.Single(logs.Messages);
            Assert.Equal("Service.Api.Exceptions.ApiExceptionHandler", entry.Category);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.DoesNotContain("INTERNAL_SECRET_SENTINEL", entry.Message);
        }
    }

    [Theory]
    [InlineData("/unmapped-route")]
    [InlineData("/__tests/errors/unexpected")]
    public async Task Normal_host_has_no_test_routes_and_returns_problem_details_for_missing_routes(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(404, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
    }

    [Fact]
    public async Task Unexpected_errors_remain_safe_with_an_unsupported_accept_header()
    {
        using var factory = new ErrorApiFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.GetAsync("/__tests/errors/unexpected");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("INTERNAL_SECRET_SENTINEL", await response.Content.ReadAsStringAsync());
    }
}
