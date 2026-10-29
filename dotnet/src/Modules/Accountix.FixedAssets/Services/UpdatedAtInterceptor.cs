using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accountix.FixedAssets.Services;

/// <summary>
/// fixed_assets.updated_at has onupdate=datetime.utcnow in the Python model:
/// stamped whenever a save changes the row.
/// </summary>
public sealed class UpdatedAtInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
                                                                          CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Stamp(DbContext? ctx)
    {
        if (ctx is null) return;
        ctx.ChangeTracker.DetectChanges();
        var now = DateTime.UtcNow;
        foreach (var e in ctx.ChangeTracker.Entries<FixedAsset>())
            if (e.State == EntityState.Modified && !e.Property(a => a.UpdatedAt).IsModified)
                e.Entity.UpdatedAt = now;
    }
}
