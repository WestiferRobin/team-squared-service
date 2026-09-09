using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Responses;
using Service.Api.Enums;
using Service.Api.Mappers;
using Xunit;

namespace Service.Api.UnitTests.Mappers;

public class ActionMapperTests
{
    [Fact]
    public void ToResponse_preserves_every_field()
    {
        var dto = new ActionDto
        {
            Id = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Name = "Mapped example",
            Type = ActionType.Delete,
            CreatedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc),
        };

        var expected = (dto.Id, dto.ItemId, dto.Name, dto.Type, dto.CreatedAt, dto.UpdatedAt);
        var response = ActionMapper.ToResponse(dto);

        Assert.Equal(expected, (dto.Id, dto.ItemId, dto.Name, dto.Type, dto.CreatedAt, dto.UpdatedAt));
        Assert.IsType<ActionResponse>(response);
        Assert.Equal(dto.Id, response.Id);
        Assert.Equal(dto.ItemId, response.ItemId);
        Assert.Equal(dto.Name, response.Name);
        Assert.Equal(dto.Type, response.Type);
        Assert.Equal(dto.CreatedAt, response.CreatedAt);
        Assert.Equal(dto.UpdatedAt, response.UpdatedAt);
    }
}
