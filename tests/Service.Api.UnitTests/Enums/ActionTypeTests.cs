using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Enums;
public class ActionTypeTests
{
    [Fact]
    public void Values_WhenEnumerated_MatchContract()
    {
        Assert.Equal(new[] { "Create", "Update", "Delete" }, Enum.GetNames<ActionType>());
        Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<ActionType>().Select(v => (int)v));
        Assert.True(Enum.IsDefined(default(ActionType)));
        Assert.False(Enum.IsDefined((ActionType)(-1)));
        Assert.False(Enum.IsDefined((ActionType)3));
    }
}
