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

namespace Service.Api.IntegrationTests.Exceptions;

public class ApiExceptionHandlerTests
{
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
        if (status < 500) Assert.DoesNotContain(logs.Messages, log => log.Level >= LogLevel.Error);
        else AssertCentralLog(logs, response);
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

    [Theory]
    [InlineData("validation", 400, "The request is invalid.")]
    [InlineData("not-found", 404, "The requested resource was not found.")]
    [InlineData("item-not-found", 404, "The requested Item was not found.")]
    [InlineData("action-not-found", 404, "The requested Action was not found.")]
    [InlineData("unexpected", 500, "An unexpected error occurred.")]
    public async Task Error_WhenAcceptUnsupported_ReturnsSafeFallbackProblem(string kind, int status, string detail)
    {
        using var logs = new RecordingLoggerProvider();
        using var factory = new ErrorApiFactory("Production").WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var response = await client.GetAsync($"/__tests/errors/{kind}");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem); Assert.Equal(status, problem.Status); Assert.Equal(detail, problem.Detail);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "INTERNAL_SECRET_SENTINEL", "InvalidOperationException", "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" }) Assert.DoesNotContain(secret, body);
        if (status < 500) Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        else AssertCentralLog(logs, response);
    }
    [Fact]
    public async Task Error_WhenUnexpected_CorrelatesHttpTraceAndCentralLog()
    {
        using var logs = new RecordingLoggerProvider();
        using var factory = new ErrorApiFactory("Production").WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__tests/errors/unexpected");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(500, problem.Status);
        Assert.Equal("An unexpected error occurred.", problem.Detail);
        AssertCentralLog(logs, response);
    }
    private static void AssertCentralLog(RecordingLoggerProvider logs, HttpResponseMessage response)
    {
        var entry = Assert.Single(logs.Entries, log => log.Category == "Service.Api.Exceptions.ApiExceptionHandler" && log.Level == LogLevel.Error);
        Assert.Null(entry.Exception);
        var message = Assert.Single(logs.Messages, log => log.Category == entry.Category).Message;
        Assert.Contains("InvalidOperationException", message);
        Assert.DoesNotContain("INTERNAL_SECRET_SENTINEL", message);
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Test-Request-Id")), entry.State["TraceId"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, entry.State["ExceptionType"]);
        Assert.DoesNotContain("INTERNAL_SECRET_SENTINEL", string.Join(" ", entry.State.Values));
    }
}
