using Npgsql;
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
    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    [InlineData("list")]
    [InlineData("get-update")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("exists")]
    [InlineData("parent-exists")]
    [InlineData("parent-list")]
    public async Task Operation_WhenCallerCancelsBlockedQuery_PropagatesWithoutCommit(string operation)
    {
        await fixture.ResetItemsAsync();
        var connection = PostgresOperations.Connection(fixture); var application = $"cancel_{Guid.NewGuid():N}";
        await using var db = PostgresOperations.Context(connection, application);
        var parent = new ItemModel { Name = "Parent" };
        var entity = new ActionModel { Item = parent, Name = "Original" };
        db.Add(entity); await db.SaveChangesAsync();
        var created = new ActionModel { Name = "Cancelled", ItemId = parent.Id };
        var repository = new ActionRepository(db);
        await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        var table = operation == "parent-exists" ? "Items" : "Actions";
        await using var command = new NpgsqlCommand($"LOCK TABLE \"{table}\" IN ACCESS EXCLUSIVE MODE", blocker, transaction);
        await command.ExecuteNonQueryAsync();
        using var caller = new CancellationTokenSource();
        async Task Run()
        {
            switch (operation)
            {
                case "create": Assert.True(await repository.TryCreateAsync(created, caller.Token)); break;
                case "get": await repository.GetByIdAsync(entity.Id, caller.Token); break;
                case "list": await repository.ListAsync(caller.Token); break;
                case "get-update": await repository.GetForUpdateAsync(entity.Id, caller.Token); break;
                case "update": entity.Name = "Changed"; await repository.UpdateAsync(entity, caller.Token); break;
                case "delete": await repository.DeleteAsync(entity, caller.Token); break;
                case "exists": await repository.ExistsAsync(entity.Id, caller.Token); break;
                case "parent-exists": await repository.ItemExistsAsync(parent.Id, caller.Token); break;
                case "parent-list": await repository.ListByItemAsync(parent.Id, caller.Token); break;
            }
        }
        var work = Run();
        try
        {
            await PostgresOperations.WaitForLockAsync(connection, application);
            Assert.False(work.IsCompleted); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(operation switch { "update" => EntityState.Modified, "delete" => EntityState.Deleted, _ => EntityState.Unchanged }, db.Entry(entity).State);
            if (operation == "create") Assert.Equal(EntityState.Added, db.Entry(created).State);
        }
        finally { caller.Cancel(); await transaction.RollbackAsync(); try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
        await using var check = PostgresOperations.Context(connection);
        Assert.Equal("Original", (await check.Actions.SingleAsync(x => x.Id == entity.Id)).Name);
        Assert.False(await check.Actions.AnyAsync(x => x.Id == created.Id));
    }
    [Theory]
    [InlineData("create")] [InlineData("update")] [InlineData("delete")]
    public async Task Write_WhenScopeHasOtherPendingChanges_CommitsEntireContext(string operation)
    {
        var connection = PostgresOperations.Connection(fixture);
        await using var db = PostgresOperations.Context(connection);
        var parent = new ItemModel { Name = "Parent" };
        var entity = new ActionModel { Item = parent, Name = "Original" };
        db.Add(entity); await db.SaveChangesAsync();
        var pending = new ItemModel { Name = "Pending scoped change" }; db.Items.Add(pending);
        var repository = new ActionRepository(db);
        if (operation == "create") { Assert.True(await repository.TryCreateAsync(new ActionModel { ItemId = parent.Id, Name = "Created" })); }
        else if (operation == "update") { entity.Name = "Updated"; await repository.UpdateAsync(entity); }
        else await repository.DeleteAsync(entity);
        await using var check = PostgresOperations.Context(connection);
        Assert.Equal(pending.Name, (await check.Items.SingleAsync(x => x.Id == pending.Id)).Name);
        Assert.Equal(EntityState.Unchanged, db.Entry(pending).State);
    }
    [Theory]
    [InlineData("create")] [InlineData("update")] [InlineData("delete")]
    public async Task Write_WhenDatabaseRejectsChange_PropagatesAndRollsBackPendingWrites(string operation)
    {
        var connection = PostgresOperations.Connection(fixture);
        await using var db = PostgresOperations.Context(connection);
        var parent = new ItemModel { Name = "Parent" }; var entity = new ActionModel { Item = parent, Name = "Original" };
        db.Add(entity); await db.SaveChangesAsync();
        var pending = new ItemModel { Name = "Must not commit" }; db.Add(pending);
        var created = new ActionModel { Name = "", ItemId = parent.Id };
        var repository = new ActionRepository(db);
        if (operation == "delete")
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE test_delete_guard (id uuid REFERENCES \"Actions\"(\"Id\") ON DELETE RESTRICT)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO test_delete_guard(id) VALUES ({entity.Id})");
        }
        try
        {
            async Task Run()
            {
                if (operation == "create") { Assert.True(await repository.TryCreateAsync(created)); }
                else if (operation == "update") { entity.Name = ""; await repository.UpdateAsync(entity); }
                else await repository.DeleteAsync(entity);
            }
            var error = await Assert.ThrowsAsync<DbUpdateException>(Run);
            Assert.Equal(operation == "delete" ? PostgresErrorCodes.ForeignKeyViolation : PostgresErrorCodes.CheckViolation,
                Assert.IsType<PostgresException>(error.InnerException).SqlState);
            Assert.Equal(operation switch { "update" => EntityState.Modified, "delete" => EntityState.Deleted, _ => EntityState.Unchanged }, db.Entry(entity).State);
            if (operation == "create") Assert.Equal(EntityState.Added, db.Entry(created).State);
            await using var check = PostgresOperations.Context(connection);
            Assert.Equal("Original", (await check.Actions.SingleAsync(x => x.Id == entity.Id)).Name);
            Assert.False(await check.Actions.AnyAsync(x => x.Id == created.Id));
            Assert.False(await check.Items.AnyAsync(x => x.Id == pending.Id));
        }
        finally { if (operation == "delete") await db.Database.ExecuteSqlRawAsync("DROP TABLE test_delete_guard"); }
    }

    [Fact]
    public async Task ListAsync_WhenDatabaseEmpty_ReturnsEmptyWithoutTracking()
    {
        await fixture.ResetItemsAsync();
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        var repository = new ActionRepository(db);
        Assert.Empty(await repository.ListAsync()); Assert.Empty(db.ChangeTracker.Entries());
    }

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
        db.ChangeTracker.Clear();
        var read = await repository.GetByIdAsync(entity.Id);
        Assert.NotNull(read);
        Assert.Equal("Original", read.Name);
        Assert.Empty(db.ChangeTracker.Entries());
        var createdAt = read.CreatedAt; // Use the value persisted at PostgreSQL precision.
        var oldUpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Actions.Where(row => row.Id == entity.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, oldUpdatedAt));
        var tracked = await repository.GetForUpdateAsync(entity.Id);
        Assert.NotNull(tracked);
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        tracked.Name = "Updated";
        tracked.Type = ActionType.Update;
        tracked.CreatedAt = DateTime.UtcNow.AddYears(-1);
        await repository.UpdateAsync(tracked);
        Assert.Equal(createdAt, tracked.CreatedAt);
        Assert.True(tracked.UpdatedAt > oldUpdatedAt);
        // Rebase only the stored timestamp, then reload: the second repository update
        // receives unchanged domain fields but must still refresh UpdatedAt.
        await db.Actions.Where(row => row.Id == entity.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, oldUpdatedAt));
        await db.Entry(tracked).ReloadAsync();
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        await repository.UpdateAsync(tracked);
        Assert.True(tracked.UpdatedAt > oldUpdatedAt);
        db.ChangeTracker.Clear();
        var updated = await repository.GetByIdAsync(entity.Id);
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal(ActionType.Update, updated.Type);
        Assert.Equal(createdAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt > oldUpdatedAt);
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
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => repository.TryCreateAsync(invalid));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        db.ChangeTracker.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.TryCreateAsync(
            new ActionModel { ItemId = parent.Id, Name = "Cancelled" }, cancellation.Token));
    }    [Fact]
    public async Task TryCreateAsync_WhenDifferentForeignKeyFails_PropagatesException()
    {
        await fixture.ResetItemsAsync();
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE test_allowed_names (name varchar(200) PRIMARY KEY)");
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Actions\" ADD CONSTRAINT \"FK_Actions_TestNames_Name\" FOREIGN KEY (\"Name\") REFERENCES test_allowed_names(name)");
            var parent = new ItemModel { Name = "Parent" }; db.Add(parent); await db.SaveChangesAsync();
            var entity = new ActionModel { ItemId = parent.Id, Name = "Not allowed" };
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => new ActionRepository(db).TryCreateAsync(entity));
            var postgres = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
            Assert.Equal("FK_Actions_TestNames_Name", postgres.ConstraintName);
            Assert.Equal(EntityState.Added, db.Entry(entity).State);
            db.ChangeTracker.Clear(); Assert.False(await db.Actions.AnyAsync(x => x.Id == entity.Id));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Actions\" DROP CONSTRAINT IF EXISTS \"FK_Actions_TestNames_Name\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE test_allowed_names");
        }
    }

}
