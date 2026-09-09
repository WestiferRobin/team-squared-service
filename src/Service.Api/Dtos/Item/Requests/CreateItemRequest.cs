using System.ComponentModel.DataAnnotations;

namespace Service.Api.Dtos.Item.Requests;

public class CreateItemRequest
{
    [Required, StringLength(200)]
    public required string Name { get; init; }
}
