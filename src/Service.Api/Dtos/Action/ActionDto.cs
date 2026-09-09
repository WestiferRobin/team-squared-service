using System.Text.Json.Serialization;
using Service.Api.Enums;

namespace Service.Api.Dtos.Action;

public class ActionDto
{
    [JsonRequired]
    public Guid Id { get; init; }
    [JsonRequired]
    public Guid ItemId { get; init; }
    [JsonRequired]
    public string Name { get; init; } = string.Empty;
    [JsonRequired]
    public ActionType Type { get; init; }
    [JsonRequired]
    public DateTime CreatedAt { get; init; }
    [JsonRequired]
    public DateTime UpdatedAt { get; init; }
}
