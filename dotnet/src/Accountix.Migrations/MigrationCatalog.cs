using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Accountix.Migrations;

/// <summary>
/// Every schema change this build knows, in order.
///
/// <list type="bullet">
/// <item>The Alembic chain (0001–0018) is what the Python app ran. A database it
/// built carries one of these names in <c>alembic_version</c>; the migrator
/// adopts it as applied up to that name.</item>
/// <item>The baseline is the schema at <see cref="BaselineVersion"/> plus the rows a
/// new installation needs. An empty database gets it, and with it every
/// Alembic revision counts as applied.</item>
/// <item><see cref="Forward"/> is the work after <see cref="MinimumAlembic"/>, for
/// databases already in production: Alembic 0016–0018 ported, then every
/// change made in .NET from now on (0019, 0020…), appended at the end.</item>
/// </list>
/// </summary>
public static class MigrationCatalog
{
    public static readonly IReadOnlyList<string> AlembicChain =
    [
        "0001_baseline",
        "0002_money_numeric",
        "0003_gl_reporting",
        "0004_invoice_dates_timestamp",
        "0005_leave_quota_days",
        "0006_pf_loan_debit",
        "0007_drop_duplicate_basic",
        "0008_capitalisation_drafts",
        "0009_payslip_taxable_pay",
        "0010_payroll_policy",
        "0011_stock_quantity_decimal",
        "0012_bank_reconciliation",
        "0013_edit_version",
        "0014_membership_module_flags",
        "0015_membership_employment",
        "0016_access_per_company",
        "0017_dotnet_data_protection",
        "0018_report_indexes",
    ];

    /// <summary>The oldest Alembic revision a database may be at (what Neon ran on 20 Oct 2026).</summary>
    public const string MinimumAlembic = "0015_membership_employment";

    /// <summary>The revision the baseline schema is at.</summary>
    public const string BaselineVersion = "0018_report_indexes";

    /// <summary>Changes after <see cref="MinimumAlembic"/>, in order. New ones go at the end.</summary>
    public static readonly IReadOnlyList<Migration> Forward =
    [
        new AccessPerCompany(),
        new SqlMigration("0017_dotnet_data_protection", "The Data Protection key ring table"),
        new SqlMigration("0018_report_indexes", "Indexes for the big pages and per-request lookups"),
    ];

    /// <summary>The text of an embedded file (Sql/x.sql → "Sql.x.sql").</summary>
    public static string Resource(string name)
    {
        var asm = typeof(MigrationCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("Accountix.Migrations." + name)
            ?? throw new InvalidOperationException($"Embedded file {name} is missing from {asm.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>One schema change. Runs inside the migrator's transaction, under its lock.</summary>
public abstract class Migration(string id, string description)
{
    public string Id { get; } = id;
    public string Description { get; } = description;

    /// <summary>What ran, recorded in schema_migrations (sha256 of the SQL).</summary>
    public abstract string Checksum { get; }

    public abstract Task ApplyAsync(MigrationContext context, CancellationToken ct);

    public override string ToString() => Id;
}

/// <summary>A change that is one embedded SQL file, <c>Sql/&lt;id&gt;.sql</c>, written to be harmless when re-run.</summary>
public sealed class SqlMigration(string id, string description) : Migration(id, description)
{
    private string Sql => MigrationCatalog.Resource($"Sql.{Id}.sql");

    public override string Checksum => MigrationCatalog.Sha256(Sql);

    public override Task ApplyAsync(MigrationContext context, CancellationToken ct) => context.ExecuteAsync(Sql, ct);
}
