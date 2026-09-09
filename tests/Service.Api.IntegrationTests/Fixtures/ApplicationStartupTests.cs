using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Service.Api.Infrastructure.Cache;
using Service.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Service.Api.IntegrationTests.Fixtures;

public class ApplicationStartupTests
{
    [Fact]
    public void Application_starts_through_the_test_host()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal("Testing", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.False(factory.Services.GetRequiredService<IConfiguration>().GetValue<bool>("OpenApi:Enabled"));

        using var first = factory.Services.CreateScope();
        Assert.IsType<ItemService>(first.ServiceProvider.GetRequiredService<IItemService>());
        Assert.IsType<ActionService>(first.ServiceProvider.GetRequiredService<IActionService>());

        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.False(lifetime.ApplicationStopped.IsCancellationRequested);
    }
    [Theory]
    [InlineData("Cache:DefaultTtlSeconds", "0", "DefaultTtlSeconds")]
    [InlineData("Cache:DefaultTtlSeconds", "86401", "DefaultTtlSeconds")]
    [InlineData("Cache:KeyPrefix", "", "KeyPrefix")]
    [InlineData("Cache:KeyPrefix", "   ", "KeyPrefix")]
    public void Invalid_cache_options_fail_actual_host_startup(string key, string value, string member)
    {
        using var factory = new ConfigurationApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { [key] = value, ["ConnectionStrings:Redis"] = "" })));
        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Equal(typeof(CacheOptions), failure.OptionsType);
        Assert.Contains(failure.Failures, message => message.Contains(member, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("127.0.0.1:1,connectTimeout=100,connectRetry=0", false)]
    public void Missing_Redis_startup_warning_matches_effective_configuration(string? connection, bool expectedWarning)
    {
        using var logs = new RecordingLoggerProvider("Service.Api");
        using var factory = new ConfigurationApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = connection }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        using var client = factory.CreateClient();
        Assert.Equal(connection, factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Redis"));
        var warnings = logs.Messages.Where(entry => entry.Category == "Service.Api"
            && entry.Message == "ConnectionStrings:Redis is missing; caching is unavailable and readiness will be degraded.").ToArray();
        if (expectedWarning)
        {
            Assert.Equal(LogLevel.Warning, Assert.Single(warnings).Level);
            var entry = Assert.Single(logs.Entries, log => log.Category == "Service.Api" && log.Level == LogLevel.Warning);
            Assert.Null(entry.Exception);
            Assert.Equal("ConnectionStrings:Redis is missing; caching is unavailable and readiness will be degraded.", entry.State["{OriginalFormat}"]);
            Assert.Single(entry.State);
        }
        else Assert.Empty(warnings);
    }
}
