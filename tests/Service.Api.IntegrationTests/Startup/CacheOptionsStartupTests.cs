using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Service.Api.Infrastructure.Cache;
using Service.Api.IntegrationTests.Fixtures;
using Xunit;
namespace Service.Api.IntegrationTests.Startup;
public class CacheOptionsStartupTests
{
    [Theory]
    [InlineData(1)] [InlineData(86400)]
    public void Startup_WhenTtlAtValidBoundary_Succeeds(int ttl)
    {
        using var factory = new ConfigurationApiFactory(overrides: new Dictionary<string, string?>
        { ["Cache:DefaultTtlSeconds"] = ttl.ToString(), ["Cache:KeyPrefix"] = "valid-prefix" });
        using var client = factory.CreateClient();
        var options = factory.Services.GetRequiredService<IOptions<CacheOptions>>().Value;
        Assert.Equal(ttl, options.DefaultTtlSeconds); Assert.Equal("valid-prefix", options.KeyPrefix);
    }
    [Fact]
    public void Startup_WhenConfigurationPrefixNull_RetainsBinderDefault()
    {
        // .NET 8 configuration binding treats a null scalar as absent.
        using var factory = new ConfigurationApiFactory(overrides: new Dictionary<string, string?> { ["Cache:KeyPrefix"] = null });
        using var client = factory.CreateClient();
        Assert.Equal("service", factory.Services.GetRequiredService<IOptions<CacheOptions>>().Value.KeyPrefix);
    }
    [Fact]
    public void Startup_WhenOptionsPrefixActuallyNull_FailsValidation()
    {
        using var factory = new ConfigurationApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<CacheOptions>(options => options.KeyPrefix = null!)));
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Equal(typeof(CacheOptions), error.OptionsType);
        Assert.Contains(error.Failures, message => message.Contains("KeyPrefix", StringComparison.Ordinal));
    }
}
