using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Service.Api.Enums;

namespace Service.Api.Dtos.Action.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class UpdateActionRequest
{
    [Required, StringLength(200)]
    public required string Name { get; init; }
    [EnumDataType(typeof(ActionType))]
    public required ActionType Type { get; init; }
}
