using System.Text.Json;
using System.Text.Json.Nodes;
using Service.Api.Dtos.Action;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Dtos.Action;
public class ActionDtoTests
{
    [Fact]
    public void Deserialize_WhenComplete_PreservesEveryField()
    {
        var dto = new ActionDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Complete", Type = ActionType.Delete, CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow };
        var result = JsonSerializer.Deserialize<ActionDto>(JsonSerializer.Serialize(dto, CacheJson.Options()), CacheJson.Options())!;
        Assert.Equal((dto.Id, dto.ItemId, dto.Name, dto.Type, dto.CreatedAt, dto.UpdatedAt), (result.Id, result.ItemId, result.Name, result.Type, result.CreatedAt, result.UpdatedAt));
    }
    [Theory]
    [InlineData("id")]
    [InlineData("itemId")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    public void Deserialize_WhenOneRequiredMemberMissing_Throws(string member)
    {
        var payload = JsonSerializer.SerializeToNode(new ActionDto(), CacheJson.Options())!.AsObject();
        Assert.True(payload.Remove(member));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ActionDto>(payload.ToJsonString(), CacheJson.Options()));
    }
    [Fact]
    public void Deserialize_WhenDefaultsOrNullNamePresent_AcceptsPresence()
    {
        var dto = new ActionDto();
        Assert.Equal(Guid.Empty, dto.Id);
        Assert.Equal(Guid.Empty, dto.ItemId);
        Assert.Equal("", dto.Name);
        Assert.Equal(ActionType.Create, dto.Type);
        Assert.Equal(default, dto.CreatedAt);
        Assert.Equal(default, dto.UpdatedAt);
        var payload = JsonSerializer.SerializeToNode(dto, CacheJson.Options())!.AsObject();
        var result = JsonSerializer.Deserialize<ActionDto>(payload.ToJsonString(), CacheJson.Options())!;
        Assert.Equal((dto.Id, dto.ItemId, dto.Name, dto.Type, dto.CreatedAt, dto.UpdatedAt), (result.Id, result.ItemId, result.Name, result.Type, result.CreatedAt, result.UpdatedAt));
        payload["name"] = null;
        Assert.Null(JsonSerializer.Deserialize<ActionDto>(payload.ToJsonString(), CacheJson.Options())!.Name);
    }
}
