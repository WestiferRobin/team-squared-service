using Microsoft.EntityFrameworkCore;
using Service.Api.Models;

namespace Service.Api.Infrastructure.Database.Repositories.Item;

public class ItemRepository : IItemRepository
{
    private readonly ServiceDbContext _dbContext;

    public ItemRepository(ServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task CreateAsync(
        ItemModel item,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Items.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ItemModel?> GetByIdAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Items
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == itemId, cancellationToken);
    }

    public async Task<IReadOnlyList<ItemModel>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Items
            .AsNoTracking()
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<ItemModel?> GetForUpdateAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Items
            .SingleOrDefaultAsync(item => item.Id == itemId, cancellationToken);
    }

    public async Task UpdateAsync(
        ItemModel item,
        CancellationToken cancellationToken = default)
    {
        // Refresh UpdatedAt even when the submitted values are unchanged.
        _dbContext.Entry(item).Property(entity => entity.Name).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        ItemModel item,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Items.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
