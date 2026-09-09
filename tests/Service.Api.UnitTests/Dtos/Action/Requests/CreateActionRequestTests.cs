using System.ComponentModel.DataAnnotations;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Dtos.Action.Requests;
public class CreateActionRequestTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    public void Validation_WhenNameAtBoundary_MatchesContract(int length, bool valid)
    {
        var name = length == -1 ? null : length == -2 ? "   " : new string('x', length);
        var request = new CreateActionRequest { Name = name!, ItemId = Guid.Empty, Type = ActionType.Create };
        Assert.Equal(valid, Validate(request));
        Assert.Equal(name, request.Name);
    }
    [Fact]
    public void Validation_WhenNameHasSurroundingSpaces_PreservesText()
    {
        var request = new CreateActionRequest { Name = " x ", ItemId = Guid.Empty, Type = ActionType.Create };
        Assert.True(Validate(request));
        Assert.Equal(" x ", request.Name);
    }
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Validation_WhenEnumAtBoundary_MatchesContract(int value, bool valid)
    {
        Assert.Equal(valid, Validate(new CreateActionRequest { Name = "Valid", Type = (ActionType)value, ItemId = Guid.NewGuid() }));
    }
    [Fact]
    public void Validation_WhenItemIdEmpty_PassesAnnotations()
    {
        Assert.True(Validate(new CreateActionRequest { Name = "Valid", Type = ActionType.Create, ItemId = Guid.Empty }));
    }
    private static bool Validate(object request) => Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), true);
}
