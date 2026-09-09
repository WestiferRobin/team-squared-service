using Service.Api.Dtos.Item;
using Service.Api.Dtos.Item.Responses;
using Service.Api.Enums;
using Service.Api.Mappers;
using Xunit;

namespace Service.Api.UnitTests.Mappers;

public class ItemMapperTests
{
    [Fact]
    public void ToResponse_preserves_every_field()
    {
        var dto = new ItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Mapped example",
            Status = ItemStatus.Archived,
            CreatedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc),
        };

        var response = ItemMapper.ToResponse(dto);

        Assert.IsType<ItemResponse>(response);
        Assert.Equal(dto.Id, response.Id);
        Assert.Equal(dto.Name, response.Name);
        Assert.Equal(dto.Status, response.Status);
        Assert.Equal(dto.CreatedAt, response.CreatedAt);
        Assert.Equal(dto.UpdatedAt, response.UpdatedAt);
    }
}
