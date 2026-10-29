using System.Linq.Expressions;
using Accountix.Core.Data.Generated;
using Accountix.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Accountix.Core.Data;

/// <summary>
/// One database, shared with the Python app during the phased move. Its
/// schema is owned by the Python app's Alembic migrations until cut-over;
/// the mapping of every table is generated from it (Data/Generated), and this
/// context never creates or alters a table.
///
/// Company isolation (the job shared/tenancy.py does in Python): every table
/// with a <c>company_id</c> column, except the platform tables, is a company
/// table —
/// <list type="bullet">
/// <item>a named query filter <c>company_id = active company</c> on every query,
/// join, include, aggregate and ExecuteUpdate/ExecuteDelete;</item>
/// <item>fail closed: querying one with no company chosen throws
/// <see cref="NoActiveCompanyException"/>;</item>
/// <item>new rows are stamped with the active company; rows never move company;
/// another company's tracked row is never written (<see cref="TenantGuard"/>);</item>
/// <item>deliberate platform-wide reads: <c>IgnoreQueryFilters([TenantFilter])</c>
/// or <see cref="CompanyContext.Unscoped"/>, both greppable.</item>
/// </list>
/// </summary>
public class AccountixDbContext(DbContextOptions<AccountixDbContext> options, CompanyContext company)
    : DbContext(options)
{
    public const string TenantFilter = "Tenant";
    public const string TenantAnnotation = "Accountix:Tenant";

    /// <summary>Tables that carry company_id but are platform machinery, never filtered (as tenancy.GLOBAL_TABLES).</summary>
    public static readonly IReadOnlySet<string> GlobalTables = new HashSet<string>
    {
        "companies", "company_memberships", "company_invitations", "global_limits", "audit_logs",
    };

    public CompanyContext Company { get; } = company;

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyMembership> Memberships => Set<CompanyMembership>();
    public DbSet<CompanyInvitation> Invitations => Set<CompanyInvitation>();
    public DbSet<GlobalLimit> GlobalLimits => Set<GlobalLimit>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    // Read by the query filter on every execution, so it can never go stale.
    // Throwing here is what makes a company query with no company fail.
    internal bool TenantBypassed => Company.Bypassed;

    internal int? TenantId => Company.CompanyId
        ?? (Company.Bypassed
            ? 0
            : throw new NoActiveCompanyException(
                "Company data was queried with no active company. Choose a company first, " +
                "or use CompanyContext.Unscoped() for deliberate platform-wide work."));

    protected override void OnModelCreating(ModelBuilder b)
    {
        GeneratedModel.Configure(b);

        // The Python app stores naive UTC (datetime.utcnow()) in
        // "timestamp without time zone": read it back as UTC, write UTC.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => DateTime.SpecifyKind(v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, DateTimeKind.Unspecified),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties()
                         .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
                prop.SetValueConverter(utc);

        // Company tables: every table with company_id except the platform ones.
        var self = Expression.Constant(this);
        foreach (var entity in b.Model.GetEntityTypes().ToList())
        {
            string? table = entity.GetTableName();
            var prop = entity.FindProperty("CompanyId");
            if (table is null || prop is null || GlobalTables.Contains(table))
                continue;
            entity.SetAnnotation(TenantAnnotation, true);
            // e => this.TenantBypassed || (int?)e.CompanyId == this.TenantId
            var e = Expression.Parameter(entity.ClrType, "e");
            Expression column = Expression.Property(e, prop.PropertyInfo!);
            if (column.Type == typeof(int))
                column = Expression.Convert(column, typeof(int?));
            var body = Expression.OrElse(
                Expression.Property(self, nameof(TenantBypassed)),
                Expression.Equal(column, Expression.Property(self, nameof(TenantId))));
            b.Entity(entity.ClrType).HasQueryFilter(TenantFilter, Expression.Lambda(body, e));
        }
    }

    public static bool IsTenantTable(IReadOnlyEntityType type) => type.FindAnnotation(TenantAnnotation)?.Value is true;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        TenantGuard.Check(ChangeTracker, Company);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        TenantGuard.Check(ChangeTracker, Company);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    internal static string ToSnake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>Write-side company isolation, checked before every save.</summary>
public static class TenantGuard
{
    public static void Check(ChangeTracker tracker, CompanyContext company)
    {
        if (company.Bypassed)
            return;
        foreach (EntityEntry entry in tracker.Entries().ToList())
        {
            if (!AccountixDbContext.IsTenantTable(entry.Metadata))
                continue;
            var prop = entry.Property("CompanyId");
            int? current = ToNullable(prop.CurrentValue);
            string what = entry.Metadata.ClrType.Name;
            switch (entry.State)
            {
                case EntityState.Added:
                    if (current is null or 0)
                    {
                        int cid = company.CompanyId
                            ?? throw new NoActiveCompanyException($"A new {what} has no company and none is active.");
                        prop.CurrentValue = prop.Metadata.ClrType == typeof(int) ? cid : (int?)cid;
                    }
                    else if (company.CompanyId is int active && current != active)
                        throw new CrossCompanyWriteException(
                            $"A new {what} names company {current} while company {active} is active.");
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                {
                    int? original = ToNullable(prop.OriginalValue);
                    if (entry.State == EntityState.Modified && prop.IsModified && original != current)
                        throw new CrossCompanyWriteException(
                            $"{what} would move from company {original} to {current}: rows never change company.");
                    if (company.CompanyId is not int act)
                        throw new NoActiveCompanyException($"{what} was changed with no active company.");
                    if (original != act)
                        throw new CrossCompanyWriteException(
                            $"{what} of company {original} was changed while company {act} is active.");
                    break;
                }
            }
        }
    }

    private static int? ToNullable(object? v) => v switch { int i => i, null => null, _ => Convert.ToInt32(v) };
}
