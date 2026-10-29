using System.Text.Json.Nodes;

namespace Accountix.Migrations.Tests;

/// <summary>
/// Databases already in production: built by the Python app, at Alembic 0015
/// (Neon) or later. Fixtures/db_0015.sql.gz is such a database (the 0015 app's
/// own build and seed, plus companies and logins in the shapes 0016 rewrites);
/// upgrade_0015.json is what the Python app's Alembic made of it.
/// </summary>
public class UpgradeTests
{
    /// <summary>The Python golden files are at Alembic 0018: compare there, whatever 0019+ add later.</summary>
    internal static readonly MigratorOptions To0018 = new() { StopAfter = "0018_report_indexes" };

    [PostgresFact]
    public async Task A_python_0015_database_upgraded_here_equals_the_same_database_upgraded_by_python()
    {
        await using var db = await TempDb.From0015Async();
        var report = await db.Migrator(To0018).MigrateAsync();

        Assert.Equal("Alembic 0015_membership_employment", report.StartedFrom);
        Assert.Equal(["0016_access_per_company", "0017_dotnet_data_protection", "0018_report_indexes"], report.Applied);
        Assert.Empty(report.Warnings);

        var golden = Fixtures.Json("upgrade_0015.json");
        AssertSchema.Same(golden["schema"]!, await Pg.SchemaAsync(db.Name), "0015 upgraded by .NET vs by Python");

        // The rows 0016 rewrites, readable first: module switches written out per
        // membership, console companies handed to their earliest active admin,
        // addresses lower-cased, alembic_version at 0018.
        var access = await Pg.AccessRowsAsync(db.Name);
        Assert.True(JsonNode.DeepEquals(golden["access"], access), access.ToJsonString());
        Assert.False(JsonNode.DeepEquals(golden["access_before"], access));
        // Then every row of every table, by digest.
        var digests = await Pg.DigestsAsync(db.Name);
        Assert.Equal(golden["data"]!.AsObject().Count, digests.AsObject().Count);
        var moved = golden["data"]!.AsObject().Where(p => !JsonNode.DeepEquals(p.Value, digests[p.Key])).Select(p => p.Key).ToList();
        Assert.True(moved.Count == 0, "tables that differ from Python's upgrade: " + string.Join(", ", moved));

        // What ran is recorded: Alembic's work adopted, the rest run here.
        var record = await db.ColumnAsync("SELECT version || ':' || how FROM schema_migrations ORDER BY version");
        Assert.Equal(MigrationCatalog.AlembicChain.Take(15).Select(v => v + ":alembic")
            .Concat(["0016_access_per_company:dotnet", "0017_dotnet_data_protection:dotnet", "0018_report_indexes:dotnet"]), record);
    }

    [PostgresFact]
    public async Task Upgraded_to_head_a_production_database_has_the_schema_of_a_new_one()
    {
        // No drift between the two paths: what Neon becomes is what a new installation gets.
        await using var db = await TempDb.From0015Async();
        var report = await db.Migrator().MigrateAsync();
        Assert.Equal(MigrationCatalog.Forward.Select(m => m.Id), report.Applied);
        AssertSchema.Same(SchemaDescription.Expected(), await Pg.SchemaAsync(db.Name), "0015 upgraded vs fresh");
    }

    [PostgresFact]
    public async Task What_0016_does_to_each_shape()
    {
        await using var db = await TempDb.From0015Async();
        await db.Migrator(To0018).MigrateAsync();

        // A membership whose company set its switches keeps them; the others get the account's.
        Assert.Equal("""{"accounting": true, "executive": false, "fbr": false, "finance": false, "fixed_assets": false, "hr": true, "inventory": false, "invoicing": false}""",
            await db.ScalarAsync("SELECT module_flags FROM company_memberships m JOIN users u ON u.id = m.user_id WHERE u.email = 'mixed.case@ref.test'"));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM company_memberships WHERE module_flags IS NULL"));
        // A NULL switch on the account is false (john.doe's fbr).
        Assert.Contains("\"fbr\": false", (string)(await db.ScalarAsync(
            "SELECT module_flags FROM company_memberships m JOIN users u ON u.id = m.user_id WHERE u.email = 'john.doe@solarkon.com'"))!);

        string Owner(string slug) => $"SELECT u.email FROM companies c JOIN users u ON u.id = c.created_by WHERE c.slug = '{slug}'";
        Assert.Equal("owner.one@ref.test", await db.ScalarAsync(Owner("console-one")));
        Assert.Equal("owner.two@ref.test", await db.ScalarAsync(Owner("console-two")));      // the earlier of two admins
        Assert.Equal("admin@gmail.com", await db.ScalarAsync(Owner("console-three")));       // its only admin is blocked
        Assert.Equal("admin@gmail.com", await db.ScalarAsync(Owner("default")));            // the super admin is a member

        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM users WHERE email <> lower(email)"));
        Assert.Equal("invitee.mixed@ref.test", await db.ScalarAsync("SELECT email FROM company_invitations WHERE status = 'sent'"));
        Assert.Equal(true, await db.ScalarAsync("SELECT to_regclass('uq_users_email_lower') IS NOT NULL"));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM user_sessions"));
    }

    [PostgresFact]
    public async Task Two_accounts_with_one_address_in_other_letters_are_left_and_reported_with_no_unique_index()
    {
        await using var db = await TempDb.From0015Async();
        await db.ExecAsync(ClashSql);
        var report = await db.Migrator(To0018).MigrateAsync();

        string warning = Assert.Single(report.Warnings);
        Assert.Contains("Twin@Ref.Test", warning);
        Assert.Contains("twin@REF.test", warning);
        Assert.Equal(["Twin@Ref.Test", "twin@REF.test"], await db.ColumnAsync("SELECT email FROM users WHERE lower(email) = 'twin@ref.test' ORDER BY id"));
        Assert.Equal("mixed.case@ref.test", await db.ScalarAsync("SELECT email FROM users WHERE id = 9"));   // others still lowered
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('uq_users_email_lower') IS NOT NULL"));
    }

    /// <summary>Two logins whose addresses differ only in letter case (users.email is unique as typed).</summary>
    internal const string ClashSql = """
        INSERT INTO users (employee_code, email, login_id, password_hash, full_name, role_id, is_active, created_at)
        SELECT 'TWIN1', 'Twin@Ref.Test', 'Twin@Ref.Test', password_hash, 'Twin One', role_id, true, created_at FROM users WHERE id = 1;
        INSERT INTO users (employee_code, email, login_id, password_hash, full_name, role_id, is_active, created_at)
        SELECT 'TWIN2', 'twin@REF.test', 'twin@REF.test', password_hash, 'Twin Two', role_id, true, created_at FROM users WHERE id = 1;
        """;

    [PostgresFact]
    public async Task A_database_at_0017_gets_only_0018()
    {
        // The state of the module reference copies (Alembic 0016/0017): built at 0018
        // here, then 0018's indexes taken away and the record set back to 0017.
        await using var db = await TempDb.From0015Async();
        await db.Migrator(To0018).MigrateAsync();
        var indexes = await db.ColumnAsync(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND indexname IN (" +
            string.Join(", ", Index0018.Select(n => $"'{n}'")) + ")");
        Assert.Equal(Index0018.Length, indexes.Count);
        await db.ExecAsync("DROP TABLE schema_migrations; UPDATE alembic_version SET version_num = '0017_dotnet_data_protection'; " +
                           string.Join(" ", Index0018.Select(n => $"DROP INDEX {n};")));

        var report = await db.Migrator(To0018).MigrateAsync();
        Assert.Equal("Alembic 0017_dotnet_data_protection", report.StartedFrom);
        Assert.Equal(["0018_report_indexes"], report.Applied);
        AssertSchema.Same(Fixtures.Json("upgrade_0015.json")["schema"]!, await Pg.SchemaAsync(db.Name), "0017 upgraded vs Python's 0018");
        Assert.Equal("0018_report_indexes", await db.ScalarAsync("SELECT version_num FROM alembic_version"));
    }

    internal static readonly string[] Index0018 =
    [
        "ix_inv_invoice_items_invoice_id", "ix_inv_purchase_invoice_items_invoice_id", "ix_additional_charges_doc",
        "ix_inv_invoices_company_date", "ix_fbr_invoice_records_invoice_status", "ix_attendance_company_date",
        "ix_leave_approvals_approver", "ix_timesheet_approvals_approver", "ix_leave_requests_user", "ix_loan_requests_user",
        "ix_audit_logs_action_created_at", "ix_asset_depreciation_asset", "ix_asset_transfers_asset",
        "ix_notification_recipients_unread", "ix_stock_ledger_product_date", "ix_inv_stock_movements_product",
    ];

    [PostgresFact]
    public async Task The_ported_revisions_are_harmless_when_re_run()
    {
        // Alembic's revisions only touch rows and objects still in the old shape;
        // so do the ports. Run each again on an upgraded database: nothing moves.
        await using var db = await TempDb.From0015Async();
        await db.Migrator().MigrateAsync();
        var schema = await Pg.SchemaAsync(db.Name);
        var data = await Pg.DigestsAsync(db.Name);

        await using (var conn = new Npgsql.NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var ctx = new MigrationContext(conn, tx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, []);
            foreach (var m in MigrationCatalog.Forward)
                await m.ApplyAsync(ctx, CancellationToken.None);
            await tx.CommitAsync();
        }
        AssertSchema.Same(schema, await Pg.SchemaAsync(db.Name), "after re-running 0016-0018");
        Assert.True(JsonNode.DeepEquals(data, await Pg.DigestsAsync(db.Name)));
    }
}
