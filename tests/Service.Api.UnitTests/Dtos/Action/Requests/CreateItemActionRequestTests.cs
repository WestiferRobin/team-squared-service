using System.ComponentModel.DataAnnotations;
using Service.Api.Dtos.Action.Requests;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Dtos.Action.Requests;
public class CreateItemActionRequestTests
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
        var request = new CreateItemActionRequest { Name = name!, Type = ActionType.Create };
        Assert.Equal(valid, Validate(request));
        Assert.Equal(name, request.Name);
    }
    [Fact]
    public void Validation_WhenNameHasSurroundingSpaces_PreservesText()
    {
        var request = new CreateItemActionRequest { Name = " x ", Type = ActionType.Create };
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
        Assert.Equal(valid, Validate(new CreateItemActionRequest { Name = "Valid", Type = (ActionType)value }));
    }
    [Fact]
    public void Contract_WhenInspectingOwnership_HasNoItemId()
    {
        Assert.Null(typeof(CreateItemActionRequest).GetProperty("ItemId"));
    }
    private static bool Validate(object request) => Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), true);
}
