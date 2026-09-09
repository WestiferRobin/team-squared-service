using Service.Api.Enums;

namespace Service.Api.Models;

public class ActionModel : ITimestampedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public ItemModel Item { get; set; } = null!;
    public required string Name { get; set; }
    public ActionType Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
