using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Service.Api.Infrastructure.Cache;
using Service.Api.Infrastructure.Cache.Item;
using Service.Api.Infrastructure.Cache.Action;
using Service.Api.Infrastructure.Database;
using Service.Api.Infrastructure.Database.Repositories.Item;
using Service.Api.Infrastructure.Database.Repositories.Action;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Services;
using Xunit;
namespace Service.Api.IntegrationTests.Startup;
public class ConfigurationTests
{
    [Fact]
    public async Task OrdinaryHost_WhenInheritedConfigurationConflicts_UsesDeterministicBaseline()
    {
        using var factory = new ConflictingApiFactory();
        using var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var cache = factory.Services.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(300, cache.DefaultTtlSeconds);
        Assert.StartsWith("service-test:", cache.KeyPrefix);
        Assert.False(config.GetValue<bool>("OpenApi:Enabled"));
        Assert.Equal("*", config["AllowedHosts"]);
        Assert.Equal("", config.GetConnectionString("Postgres"));
        Assert.Equal("", config.GetConnectionString("Redis"));
        Assert.Equal("", factory.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value.Configuration);
        using var scope = factory.Services.CreateScope();
        Assert.Equal("", scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Database.GetDbConnection().ConnectionString);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Service.Api.Exceptions.ApiExceptionHandler");
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.Null(config["Logging:LogLevel:Service.Api.Exceptions.ApiExceptionHandler"]);
        using var response = await client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var docs = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, docs.StatusCode);
    }

    private sealed class ConflictingApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            // Model an inherited provider before the ordinary per-host baseline,
            // without changing process-global environment variables.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:DefaultTtlSeconds"] = "0",
                ["Cache:KeyPrefix"] = "",
                ["AllowedHosts"] = "forbidden.example",
                ["OpenApi:Enabled"] = "true",
                ["ConnectionStrings:Postgres"] = "invalid inherited database",
                ["ConnectionStrings:Redis"] = "invalid inherited redis",
                ["Logging:LogLevel:Default"] = "None",
                ["Logging:LogLevel:Service.Api.Exceptions.ApiExceptionHandler"] = "None"
            }));
            base.ConfigureWebHost(builder);
        }
    }

    [Theory]
    [InlineData("Production", false, "Warning")]
    [InlineData("Staging", false, "Warning")]
    [InlineData("Development", true, "Information")]
    public void Configuration_WhenEnvironmentSelected_LoadsIsolatedApplicationDefaults(string environment, bool docs, string frameworkLevel)
    {
        using var factory = new ConfigurationApiFactory(environment);
        using var client = factory.CreateClient();
        Assert.Equal(environment, factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        var config = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(docs, config.GetValue<bool>("OpenApi:Enabled"));
        Assert.Equal("", config.GetConnectionString("Postgres"));
        Assert.Equal("", config.GetConnectionString("Redis"));
        Assert.Equal("Information", config["Logging:LogLevel:Default"]);
        Assert.Equal(frameworkLevel, config["Logging:LogLevel:Microsoft.AspNetCore"]);
        Assert.Equal("None", config["Logging:LogLevel:Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"]);
        Assert.False(factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware").IsEnabled(LogLevel.Error));
        var cache = factory.Services.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(300, cache.DefaultTtlSeconds); Assert.Equal("service", cache.KeyPrefix);
        Assert.Equal("", factory.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value.Configuration);
        using var scope = factory.Services.CreateScope();
        Assert.Equal("", scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Database.GetDbConnection().ConnectionString);
    }
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("127.0.0.1:1,connectTimeout=100,connectRetry=0", "127.0.0.1:1,connectTimeout=100,connectRetry=0")]
    public void Startup_WhenConnectionsOverridden_ResolvesConfiguredDependenciesWithoutConnecting(string? redis, string normalized)
    {
        const string postgres = "Host=127.0.0.1;Port=1;Database=configuration_only;Username=test;Password=test;Pooling=false";
        using var factory = new ConfigurationApiFactory(overrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres, ["ConnectionStrings:Redis"] = redis,
            ["Cache:DefaultTtlSeconds"] = "42", ["Cache:KeyPrefix"] = "hosted-test"
        });
        using var client = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(42, cache.DefaultTtlSeconds); Assert.Equal("hosted-test", cache.KeyPrefix);
        Assert.Equal(normalized, factory.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value.Configuration);
        Assert.IsType<Service.Api.Infrastructure.Cache.RedisCache>(factory.Services.GetRequiredService<ICache>());
        Assert.IsType<ItemCache>(factory.Services.GetRequiredService<IItemCache>());
        Assert.IsType<ActionCache>(factory.Services.GetRequiredService<IActionCache>());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        Assert.Equal(postgres, db.Database.GetConnectionString());
        Assert.Equal(5, db.Database.GetCommandTimeout());
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
        Assert.IsType<ItemRepository>(scope.ServiceProvider.GetRequiredService<IItemRepository>());
        Assert.IsType<ActionRepository>(scope.ServiceProvider.GetRequiredService<IActionRepository>());
        var checks = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Equal(new[] { "postgres", "redis" }, checks.Select(check => check.Name).OrderBy(name => name));
        Assert.All(checks, check => Assert.Contains("ready", check.Tags));
    }
    [Fact]
    public void Resolution_WhenScopesDiffer_PreservesApplicationScopedLifetimes()
    {
        using var factory = new ConfigurationApiFactory(); using var client = factory.CreateClient();
        using var first = factory.Services.CreateScope(); using var second = factory.Services.CreateScope();
        foreach (var type in new[] { typeof(ServiceDbContext), typeof(IItemRepository), typeof(IActionRepository), typeof(IItemService), typeof(IActionService) })
        {
            var instance = first.ServiceProvider.GetRequiredService(type);
            Assert.Same(instance, first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(instance, second.ServiceProvider.GetRequiredService(type));
        }
    }
}
