using Service.Api.Dtos.Item;
using Service.Api.Dtos.Item.Responses;

namespace Service.Api.Mappers;

public static class ItemMapper
{
    public static ItemResponse ToResponse(ItemDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        Status = dto.Status,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt,
    };
}
