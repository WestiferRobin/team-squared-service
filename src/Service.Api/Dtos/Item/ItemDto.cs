using System.Text.Json.Serialization;
using Service.Api.Enums;

namespace Service.Api.Dtos.Item;

public class ItemDto
{
    [JsonRequired]
    public Guid Id { get; init; }
    [JsonRequired]
    public string Name { get; init; } = string.Empty;
    [JsonRequired]
    public ItemStatus Status { get; init; }
    [JsonRequired]
    public DateTime CreatedAt { get; init; }
    [JsonRequired]
    public DateTime UpdatedAt { get; init; }
}
