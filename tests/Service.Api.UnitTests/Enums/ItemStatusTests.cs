using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Enums;
public class ItemStatusTests
{
    [Fact]
    public void Values_WhenEnumerated_MatchContract()
    {
        Assert.Equal(new[] { "Active", "Archived" }, Enum.GetNames<ItemStatus>());
        Assert.Equal(new[] { 0, 1 }, Enum.GetValues<ItemStatus>().Select(v => (int)v));
        Assert.True(Enum.IsDefined(default(ItemStatus)));
        Assert.False(Enum.IsDefined((ItemStatus)(-1)));
        Assert.False(Enum.IsDefined((ItemStatus)2));
    }
}
