using System.ComponentModel.DataAnnotations;
using Service.Api.Infrastructure.Cache;
using Xunit;
namespace Service.Api.UnitTests.Infrastructure.Cache;
public class CacheOptionsTests
{
    [Fact]
    public void Constructor_WhenNew_UsesDeclaredDefaults()
    {
        var options = new CacheOptions();
        Assert.Equal(300, options.DefaultTtlSeconds);
        Assert.Equal("service", options.KeyPrefix);
        Assert.True(Valid(options));
    }
    [Theory]
    [InlineData(0, false)] [InlineData(1, true)]
    [InlineData(86400, true)] [InlineData(86401, false)]
    public void Validation_WhenTtlAtBoundary_MatchesRange(int ttl, bool valid) => Assert.Equal(valid, Valid(new CacheOptions { DefaultTtlSeconds = ttl }));
    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData("   ", false)] [InlineData("custom", true)]
    public void Validation_WhenPrefixSupplied_MatchesRequired(string? prefix, bool valid) => Assert.Equal(valid, Valid(new CacheOptions { KeyPrefix = prefix! }));
    private static bool Valid(object value) => Validator.TryValidateObject(value, new ValidationContext(value), new List<ValidationResult>(), true);
}
