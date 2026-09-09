using Service.Api.Models;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Models;
public class ItemModelTests
{
    [Fact]
    public void Constructor_WhenNew_AssignsIndependentIdAndDefaults()
    {
        var first = new ItemModel { Name = "First" };
        var second = new ItemModel { Name = "Second" };
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(Guid.Empty, second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ItemStatus.Active, first.Status);
        Assert.Equal(default, first.CreatedAt);
        Assert.Equal(default, first.UpdatedAt);
        Assert.Empty(first.Actions); Assert.Empty(second.Actions); Assert.NotSame(first.Actions, second.Actions); first.Actions.Add(new ActionModel { Name = "Child" }); Assert.Empty(second.Actions);
    }
}
