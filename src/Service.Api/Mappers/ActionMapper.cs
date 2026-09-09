using Service.Api.Dtos.Action;
using Service.Api.Dtos.Action.Responses;

namespace Service.Api.Mappers;

public static class ActionMapper
{
    public static ActionResponse ToResponse(ActionDto dto) => new()
    {
        Id = dto.Id,
        ItemId = dto.ItemId,
        Name = dto.Name,
        Type = dto.Type,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt,
    };
}
