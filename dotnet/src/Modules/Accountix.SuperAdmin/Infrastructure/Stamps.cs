namespace Accountix.SuperAdmin.Infrastructure;

internal static class Stamps
{
    /// <summary>
    /// A column with onupdate=datetime.utcnow: SQLAlchemy stamps it only when
    /// the row is really written (some value changed), never on a save that
    /// changes nothing.
    /// </summary>
    public static void TouchIfChanged(this AccountixDbContext db, object entity, Action stamp)
    {
        db.ChangeTracker.DetectChanges();
        var e = db.Entry(entity);
        if (e.State == EntityState.Modified && e.Properties.Any(p => p.IsModified))
            stamp();
    }
}
