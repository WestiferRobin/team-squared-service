using Service.Api.Models;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Models;
public class ActionModelTests
{
    [Fact]
    public void Constructor_WhenNew_AssignsIndependentIdAndDefaults()
    {
        var first = new ActionModel { Name = "First" };
        var second = new ActionModel { Name = "Second" };
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(Guid.Empty, second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ActionType.Create, first.Type);
        Assert.Equal(default, first.CreatedAt);
        Assert.Equal(default, first.UpdatedAt);

    }
}
