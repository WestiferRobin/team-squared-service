using Microsoft.EntityFrameworkCore;
using Npgsql;
using Service.Api.Enums;
using Service.Api.IntegrationTests.Fixtures;
using Service.Api.Models;
using Xunit;
namespace Service.Api.IntegrationTests.Infrastructure.Database;
[Trait("Category", "Postgres")]
public class SchemaContractTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Schema_WhenMigrated_MatchesColumnsKeysAndConstraints()
    {
        await using var connection = new NpgsqlConnection(PostgresOperations.Connection(fixture)); await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT table_name, column_name, data_type, is_nullable, character_maximum_length, column_default FROM information_schema.columns WHERE table_schema='public' AND table_name IN ('Items','Actions') ORDER BY table_name, ordinal_position", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var actual = new List<string>();
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0); var name = reader.GetString(1);
                actual.Add($"{table}.{name}:{reader.GetString(2)}");
                Assert.Equal("NO", reader.GetString(3));
                if (name == "Name") Assert.Equal(200, reader.GetInt32(4));
                else Assert.True(reader.IsDBNull(4));
                Assert.True(reader.IsDBNull(5)); // IDs and timestamps are supplied by the application.
            }
            Assert.Equal(new[] { "Actions.Id:uuid", "Actions.ItemId:uuid", "Actions.Name:character varying", "Actions.Type:integer", "Actions.CreatedAt:timestamp with time zone", "Actions.UpdatedAt:timestamp with time zone", "Items.Id:uuid", "Items.Name:character varying", "Items.Status:integer", "Items.CreatedAt:timestamp with time zone", "Items.UpdatedAt:timestamp with time zone" }.OrderBy(x => x), actual.OrderBy(x => x));
        }
        await using (var command = new NpgsqlCommand("SELECT conname, contype::text, pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid IN ('\"Items\"'::regclass, '\"Actions\"'::regclass) ORDER BY conname", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var constraints = new Dictionary<string, (string Type, string Definition)>();
            while (await reader.ReadAsync()) constraints.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
            Assert.Equal(new[] { "CK_Actions_Name_NotBlank", "CK_Actions_Type", "CK_Items_Name_NotBlank", "CK_Items_Status", "FK_Actions_Items_ItemId", "PK_Actions", "PK_Items" }, constraints.Keys);
            foreach (var name in new[] { "PK_Items", "PK_Actions" }) Assert.Equal(("p", "PRIMARY KEY (\"Id\")"), constraints[name]);
            Assert.Equal(("f", "FOREIGN KEY (\"ItemId\") REFERENCES \"Items\"(\"Id\") ON DELETE CASCADE"), constraints["FK_Actions_Items_ItemId"]);
            Assert.All(constraints.Where(pair => pair.Key.StartsWith("CK_", StringComparison.Ordinal)), pair => Assert.Equal("c", pair.Value.Type));
        }
        await using (var command = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE schemaname='public' AND tablename IN ('Items','Actions') ORDER BY indexname", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var indexes = new Dictionary<string, string>(); while (await reader.ReadAsync()) indexes.Add(reader.GetString(0), reader.GetString(1));
            Assert.Equal(new[] { "IX_Actions_ItemId", "PK_Actions", "PK_Items" }, indexes.Keys);
            Assert.Contains("ON public.\"Actions\" USING btree (\"ItemId\")", indexes["IX_Actions_ItemId"]);
            Assert.DoesNotContain("UNIQUE", indexes["IX_Actions_ItemId"]);
        }
    }
    [Fact]
    public async Task Persistence_WhenDefinedEnumsAndDuplicateNames_PreservesIdsAndNumericValues()
    {
        await fixture.ResetItemsAsync();
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        var item = new ItemModel { Id = Guid.NewGuid(), Name = new string('x', 200), Status = ItemStatus.Active };
        var other = new ItemModel { Name = item.Name, Status = ItemStatus.Archived };
        db.AddRange(item, other);
        foreach (var type in new[] { ActionType.Create, ActionType.Update, ActionType.Delete }) db.Add(new ActionModel { Item = item, Name = item.Name, Type = type });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(new[] { 0, 1 }, await db.Items.OrderBy(x => x.Status).Select(x => (int)x.Status).ToArrayAsync());
        Assert.Equal(new[] { 0, 1, 2 }, await db.Actions.OrderBy(x => x.Type).Select(x => (int)x.Type).ToArrayAsync());
        Assert.Equal(item.Name, (await db.Items.SingleAsync(x => x.Id == item.Id)).Name);
    }
    [Theory]
    [InlineData(false, -1)] [InlineData(false, 2)] [InlineData(true, -1)] [InlineData(true, 3)]
    public async Task Persistence_WhenEnumOutsideDefinedRange_DatabaseRejects(bool action, int value)
    {
        await using var db = PostgresOperations.Context(PostgresOperations.Connection(fixture));
        var parent = new ItemModel { Name = "Parent" }; db.Add(parent); await db.SaveChangesAsync();
        if (action) db.Add(new ActionModel { ItemId = parent.Id, Name = "Invalid", Type = (ActionType)value });
        else db.Add(new ItemModel { Name = "Invalid", Status = (ItemStatus)value });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var provider = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, provider.SqlState);
        Assert.Equal(action ? "CK_Actions_Type" : "CK_Items_Status", provider.ConstraintName);
    }
}
