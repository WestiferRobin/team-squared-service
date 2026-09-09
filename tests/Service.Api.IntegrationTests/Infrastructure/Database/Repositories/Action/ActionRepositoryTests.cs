using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Service.Api.Enums;
using Service.Api.Infrastructure.Database;
using Service.Api.Infrastructure.Database.Repositories.Action;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Database.Repositories.Action;

[Trait("Category", "Postgres")]
public class ActionRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Writes_persist_and_reads_have_correct_tracking_and_timestamps()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IActionRepository>();
        var parent = new ItemModel { Name = "Parent" };
        db.Items.Add(parent);
        await db.SaveChangesAsync();
        var entity = new ActionModel { ItemId = parent.Id, Name = "Original" };
        Assert.True(await repository.TryCreateAsync(entity));
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
        tracked.Type = ActionType.Update;
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
        Assert.Equal(ActionType.Update, updated.Type);
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
        var repository = scope.ServiceProvider.GetRequiredService<IActionRepository>();
        var parent = new ItemModel { Name = "Parent" };
        db.Items.Add(parent);
        await db.SaveChangesAsync();
        var ids = new[] { Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002") };
        foreach (var id in ids.Reverse())
        {
            var entity = new ActionModel { Id = id, ItemId = parent.Id, Name = "Ordered" };
            Assert.True(await repository.TryCreateAsync(entity));
        }
        var time = DateTime.UtcNow.AddDays(-1);
        await db.Actions.ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.CreatedAt, time));
        await db.Actions.Where(entity => entity.Id == ids[0])
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.CreatedAt, time.AddDays(-1)));
        db.ChangeTracker.Clear();
        Assert.Equal(ids, (await repository.ListAsync()).Select(entity => entity.Id));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.True(await repository.ItemExistsAsync(parent.Id));
        Assert.False(await repository.ItemExistsAsync(Guid.NewGuid()));
        Assert.True(await repository.ExistsAsync(ids[0]));
        Assert.False(await repository.ExistsAsync(Guid.NewGuid()));
        Assert.Equal(ids, (await repository.ListByItemAsync(parent.Id)).Select(entity => entity.Id));
        Assert.Empty(await repository.ListByItemAsync(Guid.NewGuid()));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Parent_fk_failure_detaches_but_other_database_failures_propagate()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IActionRepository>();
        var missing = new ActionModel { ItemId = Guid.NewGuid(), Name = "Missing parent" };
        Assert.False(await repository.TryCreateAsync(missing));
        Assert.Equal(EntityState.Detached, db.Entry(missing).State);
        Assert.False(await repository.ExistsAsync(missing.Id));
        var parent = new ItemModel { Name = "Parent" };
        db.Items.Add(parent);
        await db.SaveChangesAsync();
        var invalid = new ActionModel { ItemId = parent.Id, Name = "", Type = ActionType.Create };
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.TryCreateAsync(invalid));
        db.ChangeTracker.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.TryCreateAsync(
            new ActionModel { ItemId = parent.Id, Name = "Cancelled" }, cancellation.Token));
    }
}
