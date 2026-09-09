using Microsoft.EntityFrameworkCore;
using Npgsql;
using Service.Api.Models;

namespace Service.Api.Infrastructure.Database.Repositories.Action;

public class ActionRepository : IActionRepository
{
    private readonly ServiceDbContext _dbContext;

    public ActionRepository(ServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> TryCreateAsync(
        ActionModel action,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Actions.Add(action);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "FK_Actions_Items_ItemId" })
        {
            // The parent can disappear between the service check and the insert.
            _dbContext.Entry(action).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<ActionModel?> GetByIdAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Actions
            .AsNoTracking()
            .SingleOrDefaultAsync(action => action.Id == actionId, cancellationToken);
    }

    public async Task<IReadOnlyList<ActionModel>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Actions
            .AsNoTracking()
            .OrderBy(action => action.CreatedAt)
            .ThenBy(action => action.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<ActionModel?> GetForUpdateAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Actions
            .SingleOrDefaultAsync(action => action.Id == actionId, cancellationToken);
    }

    public async Task UpdateAsync(
        ActionModel action,
        CancellationToken cancellationToken = default)
    {
        // Refresh UpdatedAt even when the submitted values are unchanged.
        _dbContext.Entry(action).Property(entity => entity.Name).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        ActionModel action,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Actions.Remove(action);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Actions.AsNoTracking()
            .AnyAsync(action => action.Id == actionId, cancellationToken);
    }

    public async Task<IReadOnlyList<ActionModel>> ListByItemAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Actions
            .AsNoTracking()
            .Where(action => action.ItemId == itemId)
            .OrderBy(action => action.CreatedAt)
            .ThenBy(action => action.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ItemExistsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Items.AsNoTracking()
            .AnyAsync(item => item.Id == itemId, cancellationToken);
    }
}
