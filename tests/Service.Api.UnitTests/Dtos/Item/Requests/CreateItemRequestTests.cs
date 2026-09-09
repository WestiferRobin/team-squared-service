using System.ComponentModel.DataAnnotations;
using Service.Api.Dtos.Item.Requests;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Dtos.Item.Requests;
public class CreateItemRequestTests
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
        var request = new CreateItemRequest { Name = name! };
        Assert.Equal(valid, Validate(request));
        Assert.Equal(name, request.Name);
    }
    [Fact]
    public void Validation_WhenNameHasSurroundingSpaces_PreservesText()
    {
        var request = new CreateItemRequest { Name = " x " };
        Assert.True(Validate(request));
        Assert.Equal(" x ", request.Name);
    }
    private static bool Validate(object request) => Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), true);
}
