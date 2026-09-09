using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Enums;
using Service.Api.Infrastructure.Database;
using Service.Api.Infrastructure.Database.Repositories.Item;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Database.Repositories.Item;

[Trait("Category", "Postgres")]
public class ItemRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Writes_persist_and_reads_have_correct_tracking_and_timestamps()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IItemRepository>();
        var entity = new ItemModel { Name = "Original" };
        await repository.CreateAsync(entity);
        Assert.Equal(entity.CreatedAt, entity.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, entity.CreatedAt.Kind);
        var createdAt = entity.CreatedAt;
        db.ChangeTracker.Clear();
        var read = await repository.GetByIdAsync(entity.Id);
        Assert.NotNull(read);
        Assert.Equal("Original", read.Name);
        Assert.Empty(db.ChangeTracker.Entries());
        var tracked = await repository.GetForUpdateAsync(entity.Id);
        Assert.NotNull(tracked);
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        tracked.Name = "Updated";
        tracked.Status = ItemStatus.Archived;
        tracked.CreatedAt = DateTime.UtcNow.AddYears(-1);
        await repository.UpdateAsync(tracked);
        Assert.Equal(createdAt, tracked.CreatedAt);
        var timestamp = tracked.UpdatedAt;
        await Task.Delay(10);
        await repository.UpdateAsync(tracked);
        Assert.True(tracked.UpdatedAt > timestamp);
        db.ChangeTracker.Clear();
        var updated = await repository.GetByIdAsync(entity.Id);
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal(ItemStatus.Archived, updated.Status);
        Assert.Equal(createdAt, updated.CreatedAt);
        await repository.DeleteAsync((await repository.GetForUpdateAsync(entity.Id))!);
        Assert.Null(await repository.GetByIdAsync(entity.Id));
        Assert.Null(await repository.GetForUpdateAsync(entity.Id));
    }

    [Fact]
    public async Task Lists_preserve_creation_and_id_order_without_tracking()
    {
        await fixture.ResetItemsAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IItemRepository>();
        var ids = new[] { Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002") };
        foreach (var id in ids.Reverse())
        {
            var entity = new ItemModel { Id = id, Name = "Ordered" };
            await repository.CreateAsync(entity);
        }
        var time = DateTime.UtcNow.AddDays(-1);
        await db.Items.ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.CreatedAt, time));
        await db.Items.Where(entity => entity.Id == ids[0])
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.CreatedAt, time.AddDays(-1)));
        db.ChangeTracker.Clear();
        Assert.Equal(ids, (await repository.ListAsync()).Select(entity => entity.Id));
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
