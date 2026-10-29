using Microsoft.Extensions.Logging;

namespace Accountix.Migrations;

/// <summary>
/// Alembic 0016_access_per_company, ported (migrations/versions/0016 in the
/// Python backup). Four steps, each touching only rows still in the old shape,
/// so re-running is harmless:
/// <list type="number">
/// <item><c>user_sessions</c>, one row per signed-in session, created as Alembic created it.</item>
/// <item>Module switches written out on every membership that has none, from the
/// account's switches (what the membership resolved to before), as
/// <c>json.dumps(flags, sort_keys=True)</c> wrote them.</item>
/// <item>A company made in the super admin console that recorded the super admin as
/// its owner, though they are not a member: ownership moves to its earliest
/// active admin. A super admin who is a member keeps theirs.</item>
/// <item>Emails lower-cased unless two accounts would then share an address (those
/// are left and reported); invitations lower-cased; where nothing clashes, a
/// unique index on lower(email).</item>
/// </list>
/// Set-based SQL in place of the Python loops; the result is the same row for row
/// (UpgradeTests compare it with what Alembic wrote).
/// </summary>
public sealed class AccessPerCompany() : Migration("0016_access_per_company",
    "Sessions on the server, module switches per company, console owners, lower-case emails")
{
    /// <summary>(key in module_flags, column on users), as MODULE_FLAGS in the revision.</summary>
    internal static readonly (string Key, string Column)[] ModuleFlags =
    [
        ("hr", "has_hr_access"), ("inventory", "has_inventory_access"),
        ("invoicing", "has_invoicing_access"), ("finance", "has_finance_access"),
        ("accounting", "has_accounting_access"), ("fbr", "has_fbr_access"),
        ("fixed_assets", "has_fixed_assets_access"), ("executive", "has_executive_access"),
    ];

    internal const string SessionsTable = """
        CREATE TABLE public.user_sessions (
            id SERIAL NOT NULL,
            token_hash VARCHAR(64) NOT NULL,
            user_id INTEGER NOT NULL,
            created_at TIMESTAMP WITHOUT TIME ZONE NOT NULL,
            last_seen_at TIMESTAMP WITHOUT TIME ZONE NOT NULL,
            ended_at TIMESTAMP WITHOUT TIME ZONE,
            end_reason VARCHAR(20),
            ip_address VARCHAR(64),
            user_agent VARCHAR(200),
            PRIMARY KEY (id),
            UNIQUE (token_hash),
            FOREIGN KEY(user_id) REFERENCES public.users (id)
        );
        CREATE INDEX ix_user_sessions_user_id ON public.user_sessions (user_id);
        """;

    internal const string ConsoleOwners = """
        UPDATE public.companies c
           SET created_by = (
               SELECT m.user_id FROM public.company_memberships m JOIN public.roles r ON r.id = m.role_id
                WHERE m.company_id = c.id AND m.status = 'active' AND r.name = 'admin'
                ORDER BY m.joined_at, m.id
                LIMIT 1)
         WHERE EXISTS (SELECT 1 FROM public.users u WHERE u.id = c.created_by AND u.is_super_admin = true)
           AND NOT EXISTS (SELECT 1 FROM public.company_memberships m
                            WHERE m.company_id = c.id AND m.user_id = c.created_by AND m.status <> 'removed')
           AND EXISTS (SELECT 1 FROM public.company_memberships m JOIN public.roles r ON r.id = m.role_id
                        WHERE m.company_id = c.id AND m.status = 'active' AND r.name = 'admin');
        """;

    /// <summary>Accounts whose address another account holds in other letters (they stay as they are).</summary>
    internal const string EmailClashes = """
        SELECT u.email FROM public.users u
         WHERE u.email <> lower(u.email)
           AND EXISTS (SELECT 1 FROM public.users o WHERE lower(o.email) = lower(u.email) AND o.id <> u.id)
         ORDER BY u.id;
        """;

    internal const string LowerEmails = """
        UPDATE public.users u SET email = lower(u.email)
         WHERE u.email <> lower(u.email)
           AND NOT EXISTS (SELECT 1 FROM public.users o WHERE lower(o.email) = lower(u.email) AND o.id <> u.id);
        """;

    public override string Checksum => MigrationCatalog.Sha256(
        SessionsTable + ConsoleOwners + EmailClashes + LowerEmails + string.Join(",", ModuleFlags));

    public override async Task ApplyAsync(MigrationContext db, CancellationToken ct)
    {
        if (!await db.TableExistsAsync("users", ct))
            return;

        if (!await db.TableExistsAsync("user_sessions", ct))
            await db.ExecuteAsync(SessionsTable, ct);

        if (await db.TableExistsAsync("company_memberships", ct))
            await db.ExecuteAsync(await ModuleFlagsSqlAsync(db, ct), ct);

        await db.ExecuteAsync(ConsoleOwners, ct);

        var left = await db.QueryStringsAsync(EmailClashes, ct);
        await db.ExecuteAsync(LowerEmails, ct);
        if (await db.TableExistsAsync("company_invitations", ct))
            await db.ExecuteAsync("UPDATE public.company_invitations SET email = lower(email) WHERE email <> lower(email);", ct);
        if (left.Count > 0)
        {
            db.Warn("0016: emails left as they are (another account has the same address in other letters): "
                    + string.Join(", ", left));
            return;
        }
        await db.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS uq_users_email_lower ON public.users (lower(email));", ct);
    }

    /// <summary>
    /// module_flags = the switches the account holds, for the columns it has, keys
    /// sorted, in json.dumps' layout: {"accounting": true, "executive": false, ...}.
    /// A NULL switch is false (bool(None)).
    /// </summary>
    internal static async Task<string> ModuleFlagsSqlAsync(MigrationContext db, CancellationToken ct)
    {
        var have = (await db.ColumnsAsync("users", ct)).ToHashSet(StringComparer.Ordinal);
        var parts = ModuleFlags.Where(f => have.Contains(f.Column))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => $"'\"{f.Key}\": ' || CASE WHEN COALESCE(u.{f.Column}, false) THEN 'true' ELSE 'false' END")
            .ToList();
        string value = parts.Count == 0 ? "'{}'" : "'{' || " + string.Join(" || ', ' || ", parts) + " || '}'";
        return $"""
            UPDATE public.company_memberships m SET module_flags = {value}
              FROM public.users u
             WHERE u.id = m.user_id AND m.module_flags IS NULL;
            """;
    }
}
