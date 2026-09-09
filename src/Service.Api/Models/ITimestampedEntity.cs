namespace Service.Api.Models;

public interface ITimestampedEntity
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
