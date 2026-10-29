using Accountix.TestSupport;
using System.Net;
using Accountix.Core.Access;
using Accountix.Core.Data;
using Accountix.Core.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Accountix.Tests;

/// <summary>The .NET mapping against the reference schema (tests/fixtures/reference.sql.gz, Alembic 0018).</summary>
public class SchemaTests
{
    /// <summary>reference.json names the fixture's revision (tools/Accountix.RefDb writes it); the database must be there.</summary>
    [ReferenceFact]
    public async Task The_database_is_at_the_fixture_revision()
    {
        if (!Reference.Json.TryGetProperty("alembic_version", out var expected))
            return;     // a reference.json written before the fixture existed
        await using var db = Reference.NewDb();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version_num FROM alembic_version";
        Assert.Equal(expected.GetString(), await cmd.ExecuteScalarAsync() as string);
    }

    [ReferenceFact]
    public void Every_mapped_column_exists_with_a_compatible_type()
    {
        using var db = Reference.NewDb();
        var conn = db.Database.GetDbConnection();
        conn.Open();
        var problems = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            string table = entity.GetTableName()!;
            var live = new Dictionary<string, (string Type, bool Nullable)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT column_name, data_type, is_nullable FROM information_schema.columns " +
                                  "WHERE table_schema = 'public' AND table_name = @t";
                var p = cmd.CreateParameter();
                p.ParameterName = "t";
                p.Value = table;
                cmd.Parameters.Add(p);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    live[r.GetString(0)] = (r.GetString(1), r.GetString(2) == "YES");
            }
            if (live.Count == 0)
            {
                problems.Add($"table {table} does not exist");
                continue;
            }
            var store = StoreObjectIdentifier.Table(table, null);
            foreach (var prop in entity.GetProperties())
            {
                string col = prop.GetColumnName(store)!;
                if (!live.TryGetValue(col, out var c))
                {
                    problems.Add($"{table}.{col} is mapped but missing");
                    continue;
                }
                var clr = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                bool ok = clr switch
                {
                    _ when clr == typeof(string) => c.Type is "character varying" or "text",
                    _ when clr == typeof(int) => c.Type is "integer",
                    _ when clr == typeof(bool) => c.Type is "boolean",
                    _ when clr == typeof(DateTime) => c.Type is "timestamp without time zone",
                    _ when clr == typeof(DateOnly) => c.Type is "date",
                    _ when clr == typeof(decimal) => c.Type is "numeric",
                    _ when clr == typeof(double) => c.Type is "double precision",
                    _ when clr == typeof(float) => c.Type is "real",
                    _ when clr == typeof(long) => c.Type is "bigint",
                    _ when clr == typeof(short) => c.Type is "smallint",
                    _ when clr == typeof(byte[]) => c.Type is "bytea",
                    _ when clr == typeof(TimeOnly) => c.Type is "time without time zone",
                    _ => false,
                };
                if (!ok)
                    problems.Add($"{table}.{col}: {clr.Name} mapped to {c.Type}");
                // A non-nullable value type on a nullable column crashes on the first NULL row.
                if (c.Nullable && prop.ClrType.IsValueType && Nullable.GetUnderlyingType(prop.ClrType) is null
                    && !prop.IsPrimaryKey())
                    problems.Add($"{table}.{col} can be NULL but is mapped as {prop.ClrType.Name}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}

/// <summary>
/// For every person and company, the answer the Python app gave when the
/// reference database was made (tests/fixtures/reference.facts.json "access").
/// </summary>
public class AccessParityTests
{
    [ReferenceFact]
    public async Task Role_modules_and_rights_match_the_recorded_answers()
    {
        var mismatches = new List<string>();
        int compared = 0;
        foreach (var e in Reference.Json.GetProperty("access").EnumerateArray())
        {
            if (e.GetProperty("company_id").ValueKind == System.Text.Json.JsonValueKind.Null)
                continue;
            int userId = e.GetProperty("user_id").GetInt32();
            int companyId = e.GetProperty("company_id").GetInt32();
            string who = $"{e.GetProperty("email").GetString()} in company {companyId}";

            await using var db = Reference.NewDb();
            var access = await new AccessService(db).ResolveAsync(userId, companyId);
            if (access is null || access.CompanyId != companyId)
            {
                mismatches.Add($"{who}: .NET did not open that company");
                continue;
            }
            compared++;
            if (access.RoleName != e.GetProperty("role").GetString())
                mismatches.Add($"{who}: role {access.RoleName} vs {e.GetProperty("role").GetString()}");
            if (access.IsAdmin != e.GetProperty("is_admin").GetBoolean())
                mismatches.Add($"{who}: admin {access.IsAdmin}");
            foreach (var m in e.GetProperty("modules").EnumerateObject())
                if (access.CanOpenModule(m.Name) != m.Value.GetBoolean())
                    mismatches.Add($"{who}: module {m.Name} {access.CanOpenModule(m.Name)} vs {m.Value}");
            foreach (var sec in e.GetProperty("rights").EnumerateObject())
                foreach (var act in sec.Value.EnumerateObject())
                    if (access.Can(sec.Name, act.Name) != act.Value.GetBoolean())
                        mismatches.Add($"{who}: {sec.Name}.{act.Name} {access.Can(sec.Name, act.Name)} vs {act.Value}");
        }
        Assert.True(compared >= 4, $"only {compared} person/company pairs compared");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [ReferenceFact]
    public async Task A_blocked_member_cannot_open_that_company()
    {
        int blocked = Reference.UserId(Reference.Email("blocked"));
        await using var db = Reference.NewDb();
        var access = await new AccessService(db).ResolveAsync(blocked, Reference.Company("b"));
        Assert.NotNull(access);
        Assert.NotEqual(Reference.Company("b"), access!.CompanyId);
    }

    [ReferenceFact]
    public async Task Company_tables_fail_closed_on_the_real_database()
    {
        await using var db = Reference.NewDb();
        await Assert.ThrowsAsync<NoActiveCompanyException>(() => db.UserPermissions.CountAsync());
        db.Company.Set(Reference.Company("b"));
        Assert.All(await db.UserPermissions.ToListAsync(), p => Assert.Equal(Reference.Company("b"), p.CompanyId));
        int all = await db.UserPermissions.IgnoreQueryFilters([AccountixDbContext.TenantFilter]).CountAsync();
        Assert.True(all >= await db.UserPermissions.CountAsync());
    }
}

/// <summary>The web app signs people in from the ax_session cookie over user_sessions.</summary>
public class SignInTests : IClassFixture<SignInTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        public App()
        {
            if (!Reference.Available)
                return;
            Environment.SetEnvironmentVariable("SECRET_KEY", Reference.SecretKey);
            Environment.SetEnvironmentVariable("DATABASE_URL", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__Accountix", Reference.ConnectionString);
        }
    }

    private readonly App _app;

    public SignInTests(App app) => _app = app;

    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int Restricted => Reference.UserId(Reference.Email("restricted"));

    private async Task<HttpResponseMessage> Get(string path, string? cookie, string name = TestSignIn.CookieName)
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            req.Headers.Add("Cookie", $"{name}={cookie}");
        return await client.SendAsync(req);
    }

    [ReferenceFact]
    public async Task The_session_opens_the_page_in_its_company()
    {
        var r = await Get("/net/access", TestSignIn.CookieFor(OwnerB, Reference.Company("b")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("Role here</dt><dd>admin", html);
        Assert.Contains("Ref ", html);    // company B's name in the header
        // A company made in the console is owned by the admin it names (fixed 29 Oct).
        Assert.Contains("(owner)", html, StringComparison.Ordinal);
        Assert.Contains("X-Frame-Options", string.Join(",", r.Headers.Select(h => h.Key)));
    }

    [ReferenceFact]
    public async Task A_member_sees_what_their_rights_let_them_open()
    {
        var r = await Get("/net/access", TestSignIn.CookieFor(Restricted, Reference.Company("b")));
        string html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Role here</dt><dd>manager", html);
        Assert.Matches("data-section=\"journal_vouchers\">(?:(?!</tr>).)*data-action=\"approve\" data-ok=\"0\"",
                       html.Replace("\n", " "));
        Assert.Matches("data-section=\"journal_vouchers\">(?:(?!</tr>).)*data-action=\"create\" data-ok=\"1\"",
                       html.Replace("\n", " "));
    }

    [ReferenceFact]
    public async Task No_cookie_a_tampered_one_an_ended_one_or_one_older_than_the_password_goes_to_sign_in()
    {
        string good = TestSignIn.CookieFor(OwnerB, Reference.Company("b"));
        Assert.Equal(HttpStatusCode.OK, (await Get("/net/access", good)).StatusCode);
        string tampered = good[..^3] + (good[^3] == 'A' ? "B" : "A") + good[^2..];

        // Signed correctly, but with a password fingerprint the account no longer has.
        var session = SessionCookie.Read(good, Reference.SecretKey, DateTimeOffset.UtcNow)!;
        session[SessionCookie.UserKey] = $"{OwnerB}:{SessionFingerprint.For("scrypt:an-older-hash", Reference.SecretKey)}";
        string stale = SessionCookie.Sign(session, Reference.SecretKey, DateTimeOffset.UtcNow);

        // Signed correctly, but its server row has ended (signed out elsewhere).
        string ended = TestSignIn.CookieFor(OwnerB, Reference.Company("b"));
        string hash = SessionCookie.TokenHash(SessionCookie.TokenOf(SessionCookie.Read(ended, Reference.SecretKey, DateTimeOffset.UtcNow))!);
        await using (var db = Reference.NewDb())
            await db.UserSessions.Where(s => s.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, DateTime.UtcNow).SetProperty(x => x.EndReason, "signed_out"));

        // Signed with another key.
        var other = SessionCookie.Read(good, Reference.SecretKey, DateTimeOffset.UtcNow)!;
        string foreign = SessionCookie.Sign(other, "some-other-secret-key-123456", DateTimeOffset.UtcNow);

        foreach (string? cookie in new[] { null, tampered, stale, ended, foreign })
        {
            var r = await Get("/net/access", cookie);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.StartsWith("/auth/login?next=", r.Headers.Location!.OriginalString);
        }
        // The right value under the Python app's old cookie name is nothing.
        Assert.Equal(HttpStatusCode.Redirect, (await Get("/net/access", good, "session")).StatusCode);
    }

    [ReferenceFact]
    public async Task Health_answers_without_sign_in()
    {
        var r = await Get("/net/health", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
