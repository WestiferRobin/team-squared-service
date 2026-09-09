using Microsoft.EntityFrameworkCore;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;
namespace Service.Api.IntegrationTests.Infrastructure.Database;
[Trait("Category", "Postgres")]
public class ServiceDbContextTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SaveChanges_WhenStatesChange_StampsOnlyAddedAndModifiedEntities(bool synchronous)
    {
        var connection = PostgresOperations.Connection(fixture);
        await using var db = PostgresOperations.Context(connection);
        var item = new ItemModel { Name = "Parent" };
        var action = new ActionModel { Item = item, Name = "Child" };
        var other = new ItemModel { Name = "Unchanged" };
        db.AddRange(item, action, other);
        async Task Save() { if (synchronous) db.SaveChanges(); else await db.SaveChangesAsync(); }
        await Save();
        var shared = item.CreatedAt;
        Assert.NotEqual(default, shared);
        Assert.All(new ITimestampedEntity[] { item, action, other }, entity =>
        { Assert.Equal(shared, entity.CreatedAt); Assert.Equal(shared, entity.UpdatedAt); });
        db.ChangeTracker.Clear();
        item = await db.Items.SingleAsync(x => x.Id == item.Id);
        action = await db.Actions.SingleAsync(x => x.Id == action.Id);
        other = await db.Items.SingleAsync(x => x.Id == other.Id);
        var created = item.CreatedAt; var unchangedTime = other.UpdatedAt;
        Assert.Equal(created, action.CreatedAt); Assert.Equal(created, other.CreatedAt);
        item.Name = "Changed"; action.Name = "Changed";
        item.CreatedAt = action.CreatedAt = created.AddYears(-1);
        await Save();
        Assert.Equal(created, item.CreatedAt); Assert.Equal(created, action.CreatedAt);
        Assert.True(item.UpdatedAt > created); Assert.Equal(item.UpdatedAt, action.UpdatedAt);
        Assert.Equal(unchangedTime, other.UpdatedAt);
        var itemTime = item.UpdatedAt; var actionTime = action.UpdatedAt;
        await Save();
        Assert.Equal(itemTime, item.UpdatedAt); Assert.Equal(actionTime, action.UpdatedAt);
        db.Remove(action); db.Remove(item); await Save();
        Assert.Equal(itemTime, item.UpdatedAt); Assert.Equal(actionTime, action.UpdatedAt);
        Assert.Equal(created, item.CreatedAt); Assert.Equal(created, action.CreatedAt);
        await using var check = PostgresOperations.Context(connection);
        Assert.False(await check.Items.AnyAsync(x => x.Id == item.Id));
        Assert.False(await check.Actions.AnyAsync(x => x.Id == action.Id));
        Assert.Equal(unchangedTime, (await check.Items.SingleAsync(x => x.Id == other.Id)).UpdatedAt);
    }
}
