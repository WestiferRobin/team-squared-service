using Service.Api.Exceptions;
using Service.Api.Exceptions.Item;
using Service.Api.Exceptions.Action;
using Xunit;
namespace Service.Api.UnitTests.Exceptions;
public class DomainExceptionTests
{
    [Fact]
    public void Constructor_WhenSafeMessageSupplied_PreservesMessage()
    {
        Assert.Equal("Safe missing", new NotFoundException("Safe missing").Message);
        Assert.Equal("Safe validation", new RequestValidationException("Safe validation").Message);
    }
    [Fact]
    public void Constructor_WhenResourceMissing_StoresIdAndFixedSafeMessage()
    {
        var id = Guid.NewGuid();
        var item = new ItemNotFoundException(id);
        var action = new ActionNotFoundException(id);
        Assert.IsAssignableFrom<NotFoundException>(item);
        Assert.IsAssignableFrom<NotFoundException>(action);
        Assert.Equal(id, item.ItemId); Assert.Equal(id, action.ActionId);
        Assert.Equal("The requested Item was not found.", item.Message);
        Assert.Equal("The requested Action was not found.", action.Message);
    }
}
