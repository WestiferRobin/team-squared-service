using System.ComponentModel.DataAnnotations;
using Service.Api.Enums;

namespace Service.Api.Dtos.Item.Requests;

public class UpdateItemRequest
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [EnumDataType(typeof(ItemStatus))]
    public required ItemStatus Status { get; init; }
}
