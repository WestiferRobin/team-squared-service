using Service.Api.Enums;

namespace Service.Api.Models;

public class ItemModel : ITimestampedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<ActionModel> Actions { get; set; } = new List<ActionModel>();
}
