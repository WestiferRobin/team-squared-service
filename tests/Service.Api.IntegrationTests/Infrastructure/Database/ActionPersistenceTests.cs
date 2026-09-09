using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Service.Api.Enums;
using Service.Api.Infrastructure.Database;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;

namespace Service.Api.IntegrationTests.Infrastructure.Database;

[Trait("Category", "Postgres")]
public class ActionPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Action_round_trips_with_stable_ownership_and_UTC_timestamps(bool synchronous)
    {
        var parent = new ItemModel { Name = "Parent" };
        var action = new ActionModel { Item = parent, Name = "Original", Type = ActionType.Create };
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            db.Actions.Add(action);
            if (synchronous) db.SaveChanges(); else await db.SaveChangesAsync();
        }
        DateTime created;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            var saved = await db.Actions.SingleAsync(a => a.Id == action.Id);
            Assert.Equal(parent.Id, saved.ItemId);
            Assert.Equal("Original", saved.Name);
            Assert.Equal(ActionType.Create, saved.Type);
            Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
            Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind);
            Assert.InRange(saved.CreatedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
            Assert.Equal(saved.CreatedAt, saved.UpdatedAt);
            created = saved.CreatedAt;
            saved.CreatedAt = created.AddYears(-1);
            saved.Name = "Changed";
            saved.Type = ActionType.Delete;
            if (synchronous) db.SaveChanges(); else await db.SaveChangesAsync();
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            var saved = await db.Actions.SingleAsync(a => a.Id == action.Id);
            Assert.Equal(created, saved.CreatedAt);
            Assert.True(saved.UpdatedAt > created);
            Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind);
            Assert.Equal(parent.Id, saved.ItemId);
            Assert.Equal("Changed", saved.Name);
            Assert.Equal(ActionType.Delete, saved.Type);
            db.Actions.Remove(saved);
            await db.SaveChangesAsync();
        }
        using var finalScope = fixture.Factory.Services.CreateScope();
        var final = finalScope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        Assert.False(await final.Actions.AnyAsync(a => a.Id == action.Id));
        Assert.True(await final.Items.AnyAsync(i => i.Id == parent.Id));
    }

    [Theory]
    [InlineData(null, 0, "23502")]
    [InlineData("", 0, "23514")]
    [InlineData(" \t\n", 0, "23514")]
    [InlineData("valid", 99, "23514")]
    [InlineData("too-long", 0, "22001")]
    public async Task PostgreSQL_enforces_name_and_type(string? name, int type, string state)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var parent = new ItemModel { Name = "Parent" };
        db.Items.Add(parent);
        await db.SaveChangesAsync();
        db.Actions.Add(new ActionModel { ItemId = parent.Id, Name = name == "too-long" ? new string('x', 201) : name!, Type = (ActionType)type });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(state, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSQL_rejects_empty_or_nonexistent_parent(bool empty)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        db.Actions.Add(new ActionModel { ItemId = empty ? Guid.Empty : Guid.NewGuid(), Name = "Orphan" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
        Assert.Equal("FK_Actions_Items_ItemId", postgres.ConstraintName);
    }

    [Fact]
    public async Task PostgreSQL_rejects_null_parent()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Actions\" (\"Id\", \"ItemId\", \"Name\", \"Type\", \"CreatedAt\", \"UpdatedAt\") VALUES ({Guid.NewGuid()}, NULL, 'Orphan', 0, now(), now())"));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
        Assert.Equal("ItemId", error.ColumnName);
    }

    [Fact]
    public async Task Multiple_duplicate_actions_load_and_database_cascades_only_the_deleted_parent()
    {
        var parent = new ItemModel { Name = "Parent" };
        var other = new ItemModel { Name = "Other" };
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
            db.Actions.AddRange(new ActionModel { Item = parent, Name = "Duplicate", Type = ActionType.Create },
                new ActionModel { Item = parent, Name = "Duplicate", Type = ActionType.Create },
                new ActionModel { Item = other, Name = "Duplicate", Type = ActionType.Create });
            await db.SaveChangesAsync();
        }
        using var check = fixture.Factory.Services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var loaded = await context.Items.AsNoTracking().Include(i => i.Actions).SingleAsync(i => i.Id == parent.Id);
        Assert.Equal(2, loaded.Actions.Count);
        Assert.All(loaded.Actions, a => { Assert.Equal(parent.Id, a.ItemId); Assert.Same(loaded, a.Item); });
        // Bulk delete bypasses EF client cascades: PostgreSQL must remove the children.
        await context.Items.Where(i => i.Id == parent.Id).ExecuteDeleteAsync();
        Assert.False(await context.Actions.AnyAsync(a => a.ItemId == parent.Id));
        Assert.Single(await context.Actions.Where(a => a.ItemId == other.Id).ToListAsync());
        Assert.True(await context.Items.AnyAsync(i => i.Id == other.Id));
    }

    [Fact]
    public async Task Normal_EF_update_cannot_reparent_an_action()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var action = new ActionModel { Item = new ItemModel { Name = "Owner" }, Name = "Owned" };
        var other = new ItemModel { Name = "Other" };
        db.Actions.Add(action);
        db.Items.Add(other);
        await db.SaveChangesAsync();
        var original = action.ItemId;
        action.ItemId = other.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(original, (await db.Actions.SingleAsync(a => a.Id == action.Id)).ItemId);
    }
}
