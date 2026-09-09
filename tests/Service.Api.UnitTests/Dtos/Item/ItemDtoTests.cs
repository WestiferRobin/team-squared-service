using System.Text.Json;
using System.Text.Json.Nodes;
using Service.Api.Dtos.Item;
using Service.Api.Enums;
using Xunit;
namespace Service.Api.UnitTests.Dtos.Item;
public class ItemDtoTests
{
    [Fact]
    public void Deserialize_WhenComplete_PreservesEveryField()
    {
        var dto = new ItemDto { Id = Guid.NewGuid(), Name = "Complete", Status = ItemStatus.Archived, CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow };
        var result = JsonSerializer.Deserialize<ItemDto>(JsonSerializer.Serialize(dto, CacheJson.Options()), CacheJson.Options())!;
        Assert.Equal((dto.Id, dto.Name, dto.Status, dto.CreatedAt, dto.UpdatedAt), (result.Id, result.Name, result.Status, result.CreatedAt, result.UpdatedAt));
    }
    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("status")]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    public void Deserialize_WhenOneRequiredMemberMissing_Throws(string member)
    {
        var payload = JsonSerializer.SerializeToNode(new ItemDto(), CacheJson.Options())!.AsObject();
        Assert.True(payload.Remove(member));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ItemDto>(payload.ToJsonString(), CacheJson.Options()));
    }
    [Fact]
    public void Deserialize_WhenDefaultsOrNullNamePresent_AcceptsPresence()
    {
        var dto = new ItemDto();
        Assert.Equal(Guid.Empty, dto.Id);

        Assert.Equal("", dto.Name);
        Assert.Equal(ItemStatus.Active, dto.Status);
        Assert.Equal(default, dto.CreatedAt);
        Assert.Equal(default, dto.UpdatedAt);
        var payload = JsonSerializer.SerializeToNode(dto, CacheJson.Options())!.AsObject();
        var result = JsonSerializer.Deserialize<ItemDto>(payload.ToJsonString(), CacheJson.Options())!;
        Assert.Equal((dto.Id, dto.Name, dto.Status, dto.CreatedAt, dto.UpdatedAt), (result.Id, result.Name, result.Status, result.CreatedAt, result.UpdatedAt));
        payload["name"] = null;
        Assert.Null(JsonSerializer.Deserialize<ItemDto>(payload.ToJsonString(), CacheJson.Options())!.Name);
    }
}
