using Microsoft.EntityFrameworkCore;
using Service.Api.Models;

namespace Service.Api.Infrastructure.Database;

public class ServiceDbContext : DbContext
{
    public ServiceDbContext(
        DbContextOptions<ServiceDbContext> options)
        : base(options)
    {
    }

    public DbSet<ItemModel> Items => Set<ItemModel>();
    public DbSet<ActionModel> Actions => Set<ActionModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServiceDbContext).Assembly);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestampedEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                var createdAt = entry.Property(entity => entity.CreatedAt);
                createdAt.CurrentValue = createdAt.OriginalValue;
                createdAt.IsModified = false;
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
