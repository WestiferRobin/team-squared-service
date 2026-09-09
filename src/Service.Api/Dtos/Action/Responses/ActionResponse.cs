using Service.Api.Enums;

namespace Service.Api.Dtos.Action.Responses;

public class ActionResponse
{
    public Guid Id { get; init; }
    public Guid ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ActionType Type { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
