using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Accountix.SuperAdmin.Tests;

/// <summary>Manage Companies: create + provision, the named admin owns it, module entitlements, edit.</summary>
public class CompaniesTests
{
    private static (string, string)[] Form(string name, string slug, string email, string? adminName = null, string? password = null, string plan = "free") =>
        [("name", name), ("slug", slug), ("plan_name", plan), ("admin_email", email), ("admin_name", adminName ?? ""), ("password", password ?? "")];

    [ReferenceFact]
    public async Task A_console_company_is_provisioned_and_owned_by_the_admin_it_names()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string slug = $"console-{S.Tag()}";
        string email = $"{slug}@example.com";
        var r = await S.PostAsync(c, "/superadmin/companies/", "/superadmin/companies/",
            Form(" Console Books Co ", " " + slug.ToUpperInvariant() + " ", " " + email.ToUpperInvariant(), " Console Admin ", plan: " "));
        Assert.Equal("/superadmin/companies/", S.Location(r));
        string flash = await S.FollowAsync(c, r);
        Assert.Contains($"Company 'Console Books Co' created. Admin: {email}. New user — password: ", flash);
        string password = flash[(flash.IndexOf("New user — password: ", StringComparison.Ordinal) + 21)..][..12];

        await using var db = S.Db();
        var co = await db.Companies.AsNoTracking().SingleAsync(x => x.Slug == slug);
        var admin = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.Equal(admin.Id, co.CreatedBy);                         // ACCESS_REVIEW #3: the named admin owns it
        Assert.Equal("free", co.PlanName);
        Assert.True(co.IsActive == true && co.ModAccountingEnabled == true && co.ModExecutiveEnabled == true);
        Assert.Equal(("Pakistan", "PKR", "Rs.", "Asia/Karachi"), (co.Country, co.Currency, co.CurrencySymbol, co.Timezone));
        Assert.Equal("Console Admin", admin.FullName);
        Assert.Matches("^[A-Za-z0-9]{12}$", password);
        Assert.True(PasswordHasher.Verify(admin.PasswordHash, password));
        Assert.True(admin.HasAccountingAccess == true && admin.HasExecutiveAccess == true && admin.HasFbrAccess == true);
        var m = await db.Memberships.AsNoTracking().SingleAsync(x => x.CompanyId == co.Id);
        Assert.Equal(admin.Id, m.UserId);
        Assert.Equal(CompanyMembership.StatusActive, m.Status);
        Assert.Equal(admin.EmployeeCode, m.EmployeeCode);
        Assert.Equal(await S.RoleIdAsync(Role.Admin), m.RoleId);
        Assert.Equal("{\"accounting\": true, \"executive\": true, \"fbr\": true, \"finance\": true, \"fixed_assets\": true, \"hr\": true, \"inventory\": true, \"invoicing\": true}", m.ModuleFlags);

        // provision_company ran: periods, chart, numbering (the report pickers were empty without it).
        Assert.True(await db.Set<AccountingPeriod>().IgnoreQueryFilters().CountAsync(p => p.CompanyId == co.Id) >= 1);
        Assert.True(await db.Set<ChartOfAccount>().IgnoreQueryFilters().CountAsync(a => a.CompanyId == co.Id) > 50);
        Assert.True(await db.Set<VoucherNumber>().IgnoreQueryFilters().CountAsync(v => v.CompanyId == co.Id) >= 14);

        // It shows in the list, with its active member count.
        string list = S.Text(await c.GetStringAsync("/superadmin/companies/"));
        Assert.Contains($"<code>{slug}</code>", list);
    }

    [ReferenceFact]
    public async Task An_existing_email_becomes_the_admin_without_a_new_account()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string email = $"has.{S.Tag()}@example.com";
        int uid = await S.NewUserAsync(email);
        int other = await S.NewCompanyAsync("Elsewhere", createdBy: uid);
        await S.AddMembershipAsync(other, uid, Role.Admin);
        string slug = $"existing-{S.Tag()}";
        var r = await S.PostAsync(c, "/superadmin/companies/", "/superadmin/companies/", Form("Existing Admin Co", slug, email.ToUpperInvariant(), password: "x"));
        string flash = await S.FollowAsync(c, r);
        Assert.Contains($"Company 'Existing Admin Co' created. Admin: {email}.", flash);
        Assert.DoesNotContain("New user", flash);
        await using var db = S.Db();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));
        var co = await db.Companies.AsNoTracking().SingleAsync(x => x.Slug == slug);
        Assert.Equal(uid, co.CreatedBy);
        // A further company: switches from the admin role (all on), no rights rows (admins pass every check).
        var m = await db.Memberships.AsNoTracking().SingleAsync(x => x.CompanyId == co.Id);
        Assert.Contains("\"accounting\": true", m.ModuleFlags);
        Assert.False(await db.UserPermissions.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == co.Id));
    }

    [ReferenceFact]
    public async Task Refusals_in_pythons_words()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string taken = $"taken-{S.Tag()}";
        await S.NewCompanyAsync("Taken", taken);
        string loginTaken = $"lid.{S.Tag()}@example.com";
        int other = await S.NewUserAsync();
        await using (var db = S.Db())
            await db.Users.Where(u => u.Id == other).ExecuteUpdateAsync(s => s.SetProperty(u => u.LoginId, loginTaken));

        async Task<string> Try(params (string, string)[] f)
        {
            var r = await S.PostAsync(c, "/superadmin/companies/", "/superadmin/companies/", f);
            Assert.Equal("/superadmin/companies/", S.Location(r));
            return await S.FollowAsync(c, r);
        }
        string fresh = $"new.{S.Tag()}@example.com";
        Assert.Contains("Company name, slug and admin email are required.", await Try(Form("", "abc", fresh)));
        Assert.Contains("Company name, slug and admin email are required.", await Try(Form("X", "abc", " ")));
        Assert.Contains("Company address must be 2-62 characters: lowercase letters, digits and hyphens only.", await Try(Form("X", "-bad", fresh)));
        Assert.Contains("Company address must be 2-62 characters: lowercase letters, digits and hyphens only.", await Try(Form("X", "a", fresh)));
        Assert.Contains($"A company with slug '{taken}' already exists.", await Try(Form("X", taken, fresh)));
        Assert.Contains("Password must be at least 8 characters.", await Try(Form("X", $"ok-{S.Tag()}", fresh, password: "short")));
        Assert.Contains("Enter a valid admin email.", await Try(Form("X", $"ok-{S.Tag()}", "no-at-sign")));
        Assert.Contains($"The login id {loginTaken} is already taken by another account.", await Try(Form("X", $"ok-{S.Tag()}", loginTaken)));
        await using var db2 = S.Db();
        Assert.False(await db2.Users.AnyAsync(u => u.Email == fresh));
    }

    [ReferenceFact]
    public async Task The_edit_form_is_the_whole_truth_for_the_modules()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int uid = await S.NewUserAsync();
        int cid = await S.NewCompanyAsync($"Entitled Co {S.Tag()}", createdBy: uid);
        await S.AddMembershipAsync(cid, uid, Role.Admin);
        string page = $"/superadmin/companies/{cid}/";
        string html = await c.GetStringAsync(page);
        Assert.Contains("name=\"mod_hr\"", html);
        Assert.Contains("name=\"mod_fbr\"", html);
        Assert.Contains("<span>FBR Digital Invoicing</span>", html);

        await S.PostAsync(c, page, page, ("name", "Entitled Co"), ("plan_name", "free"), ("is_active", "on"), ("mod_hr", "on"), ("mod_finance", "on"));
        Assert.Equal(["finance", "hr"], await EnabledAsync(cid));
        await S.PostAsync(c, page, page, ("name", "Entitled Co"), ("plan_name", "free"), ("is_active", "on"),
                          ("mod_hr", "on"), ("mod_finance", "on"), ("mod_inventory", "on"));
        Assert.Equal(["finance", "hr", "inventory"], await EnabledAsync(cid));
        // What is saved survives a reload of the page.
        html = await c.GetStringAsync(page);
        Assert.Contains("name=\"mod_inventory\"\n                 checked=\"checked\"", html);
        Assert.DoesNotContain("name=\"mod_accounting\"\n                 checked", html);
    }

    private static async Task<List<string>> EnabledAsync(int cid)
    {
        await using var db = S.Db();
        var co = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == cid);
        return Modules.All.Select(m => m.Key).Where(co.ModuleEnabled).Order().ToList();
    }

    [ReferenceFact]
    public async Task Edit_saves_the_profile_fields_with_pythons_fallbacks()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int cid = await S.NewCompanyAsync($"Profile Co {S.Tag()}");
        string page = $"/superadmin/companies/{cid}/";
        var r = await S.PostAsync(c, page, page, ("name", "  Renamed Co "), ("plan_name", ""), ("max_members", " 7 "),
            ("address", " 1 Mall Road "), ("city", "Lahore"), ("country", ""), ("phone", ""), ("email", "co@example.com"),
            ("website", ""), ("tax_id", "NTN-1"), ("registration_number", ""), ("logo_url", ""), ("currency", ""), ("currency_symbol", ""));
        Assert.Contains("Company updated.", await S.FollowAsync(c, r));
        await using var db = S.Db();
        var co = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == cid);
        Assert.Equal("Renamed Co", co.Name);
        Assert.Equal("free", co.PlanName);
        Assert.Equal(7, co.MaxMembers);
        Assert.Equal(" 1 Mall Road ", co.Address);                 // not stripped, as Python
        Assert.Equal("Lahore", co.City);
        Assert.Equal(("Pakistan", "PKR", "Rs."), (co.Country, co.Currency, co.CurrencySymbol));
        Assert.Null(co.Phone);
        Assert.Equal("NTN-1", co.TaxId);
        Assert.False(co.IsActive);                                 // absent checkbox: off
        Assert.False(co.ModHrEnabled);
        Assert.NotNull(co.UpdatedAt);

        // A blank name keeps the name; a bad member limit saves nothing.
        r = await S.PostAsync(c, page, page, ("name", " "), ("max_members", "many"), ("is_active", "on"));
        Assert.Contains("Max members must be a whole number.", await S.FollowAsync(c, r));
        await using var db2 = S.Db();
        co = await db2.Companies.AsNoTracking().FirstAsync(x => x.Id == cid);
        Assert.Equal("Renamed Co", co.Name);
        Assert.False(co.IsActive);
        r = await S.PostAsync(c, page, page, ("name", " "), ("max_members", ""), ("is_active", "on"));
        await using var db3 = S.Db();
        co = await db3.Companies.AsNoTracking().FirstAsync(x => x.Id == cid);
        Assert.Equal("Renamed Co", co.Name);
        Assert.Null(co.MaxMembers);
        Assert.True(co.IsActive);
    }

    /// <summary>
    /// The definition of done's parity check, now a fixed expectation: a company made by the .NET
    /// console has the books Python's console gave the same company on 29 Oct 2026 (Golden/ConsoleCompanyBooks.json):
    /// every company table (chart, numbering, periods, settings, asset categories, the four invoice
    /// templates…), the company, membership and admin account rows, and the audit events.
    /// </summary>
    [ReferenceFact]
    public async Task A_console_company_gets_the_books_python_gave_the_same_company()
    {
        string slug = $"net-{S.Tag()}";
        await using (var host = await S.HostAsync())
        {
            var r = await S.PostAsync(S.Super(host), "/superadmin/companies/", "/superadmin/companies/",
                Form("Parity Co", slug, $"{slug}@example.com", "Parity Admin", S.Pw, plan: "gold"));
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        var books = await BooksAsync(slug);
        Assert.Equal(4, books["tables"]!["invoice_templates"]!.AsArray().Count);
        Assert.True(books["tables"]!.AsObject().Sum(t => t.Value!.AsArray().Count) > 180);
        // One deliberate difference (E2E finding F6): .NET provisioning also adds hr_app/seed.py's three
        // leave types, which Python's provision_company never wrote. Checked here, then left out of the comparison.
        var leave = books["tables"]!["leave_types"]!.AsArray().Select(t => $"{t!["code"]}:{t["default_quota"]}:{t["max_carry_forward"]}").Order();
        Assert.Equal(["AL:20:10", "CL:12:0", "SL:10:5"], leave);
        books["tables"]!.AsObject().Remove("leave_types");
        Golden.Match("ConsoleCompanyBooks", books);
    }

    /// <summary>
    /// Everything the console wrote for the company with this slug, comparable across companies and days:
    /// per company table its rows (ids, company and timestamps left out, the parent account by code), sorted;
    /// the company, membership and admin rows (role by name); the audit events by kind and count. Dates are
    /// written relative to today ("Y+0-07-01" = 1 July of this year, "TODAY").
    /// </summary>
    internal static async Task<System.Text.Json.Nodes.JsonObject> BooksAsync(string slug)
    {
        await using var db = S.Db();
        int cid = await db.Companies.Where(x => x.Slug == slug).Select(x => x.Id).SingleAsync();
        int admin = await db.Users.Where(u => u.Email == $"{slug}@example.com").Select(u => u.Id).SingleAsync();
        var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        await using var conn = new NpgsqlConnection(Reference.ConnectionString);
        await conn.OpenAsync();
        var tableNames = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'company_id' " +
            "AND table_name NOT IN ('company_memberships', 'audit_logs', 'company_invitations') ORDER BY table_name", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) tableNames.Add(rd.GetString(0));

        var tables = new System.Text.Json.Nodes.JsonObject();
        foreach (string t in tableNames)
        {
            var rows = await RowsAsync(conn, t, cid);
            if (rows.Count > 0)
                tables[t] = new System.Text.Json.Nodes.JsonArray(rows.Select(r => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonNode.Parse(r)).ToArray());
        }
        System.Text.Json.Nodes.JsonObject Row(string json, string[] drop)
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            foreach (string d in drop) o.Remove(d);
            if (o["role_id"] is { } rid) { o.Remove("role_id"); o["role"] = roles[rid.GetValue<int>()]; }
            return new System.Text.Json.Nodes.JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        }
        var books = new System.Text.Json.Nodes.JsonObject
        {
            ["tables"] = tables,
            ["company"] = Row(await RowJsonAsync(conn, "companies", "id", cid), ["id", "slug", "created_by", "created_at", "updated_at"]),
            ["membership"] = Row(await RowJsonAsync(conn, "company_memberships", "company_id", cid), ["id", "company_id", "user_id", "employee_code", "joined_at"]),
            ["admin"] = Row(await RowJsonAsync(conn, "users", "id", admin), ["id", "employee_code", "email", "login_id", "password_hash", "created_at", "updated_at"]),
            ["audit"] = new System.Text.Json.Nodes.JsonArray((await AuditAsync(conn, cid, admin)).Select(a => (System.Text.Json.Nodes.JsonNode?)a).ToArray()),
        };
        return (System.Text.Json.Nodes.JsonObject)RelativeDates(books)!;
    }

    /// <summary>Date strings ("2026-07-01", "2026-07-01T00:00:00") and period years ("2026", "FY 2026") relative to
    /// this year ("Y+0-07-01", "FY Y+0"); today's date (± a day for the time zone) as TODAY.</summary>
    private static System.Text.Json.Nodes.JsonNode? RelativeDates(System.Text.Json.Nodes.JsonNode? node)
    {
        var today = DateTime.UtcNow.Date;
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                foreach (string k in o.Select(p => p.Key).ToList()) o[k] = RelativeDates(o[k]?.DeepClone());
                return o;
            case System.Text.Json.Nodes.JsonArray a:
                return new System.Text.Json.Nodes.JsonArray(a.Select(x => RelativeDates(x?.DeepClone())).ToArray());
            case System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.String:
                string s = v.GetValue<string>();
                var fy = System.Text.RegularExpressions.Regex.Match(s, @"^(FY )?(20\d\d)$");    // a period's fiscal_year / period_name
                if (fy.Success)
                {
                    int dy = int.Parse(fy.Groups[2].Value) - today.Year;
                    return System.Text.Json.Nodes.JsonValue.Create($"{fy.Groups[1].Value}Y{(dy < 0 ? "" : "+")}{dy}");
                }
                var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d{4})-(\d{2})-(\d{2})(T00:00:00)?$");
                if (!m.Success) return System.Text.Json.Nodes.JsonValue.Create(s);
                var d = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                if (Math.Abs((d - today).TotalDays) <= 1) return System.Text.Json.Nodes.JsonValue.Create("TODAY");
                int off = d.Year - today.Year;
                return System.Text.Json.Nodes.JsonValue.Create($"Y{(off < 0 ? "" : "+")}{off}{s[4..]}");
            default:
                return node is null ? null : System.Text.Json.Nodes.JsonNode.Parse(node.ToJsonString());
        }
    }

    /// <summary>A company's rows of one table as comparable text: ids, company and timestamps left out, the parent account by code.</summary>
    private static async Task<List<string>> RowsAsync(NpgsqlConnection conn, string table, int companyId)
    {
        string sql = table == "chart_of_accounts"
            ? "SELECT to_jsonb(t) || jsonb_build_object('parent_code', p.code) FROM chart_of_accounts t LEFT JOIN chart_of_accounts p ON p.id = t.parent_id WHERE t.company_id = @c"
            : $"SELECT to_jsonb(t) FROM \"{table}\" t WHERE t.company_id = @c";
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("c", companyId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var o = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rd.GetString(0))!;
            foreach (string k in o.Keys.ToList())
                if (k is "id" or "company_id" || k.EndsWith("_id", StringComparison.Ordinal) || k.EndsWith("_at", StringComparison.Ordinal))
                    o.Remove(k);
            rows.Add(JsonSerializer.Serialize(o.OrderBy(kv => kv.Key).ToDictionary()));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static async Task<string> RowJsonAsync(NpgsqlConnection conn, string table, string key, int value)
    {
        await using var cmd = new NpgsqlCommand($"SELECT to_jsonb(t)::text FROM \"{table}\" t WHERE t.{key} = @v", conn);
        cmd.Parameters.AddWithValue("v", value);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> AuditAsync(NpgsqlConnection conn, int companyId, int adminId)
    {
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT action, module, entity_type, count(*) FROM audit_logs " +
            "WHERE company_id = @c OR (entity_type = 'User' AND entity_id = @u) " +
            "GROUP BY action, module, entity_type ORDER BY action, module, entity_type", conn);
        cmd.Parameters.AddWithValue("c", companyId);
        cmd.Parameters.AddWithValue("u", adminId);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add($"{rd.GetString(0)} {(rd.IsDBNull(1) ? "" : rd.GetString(1))} {(rd.IsDBNull(2) ? "" : rd.GetString(2))} ×{rd.GetInt64(3)}");
        return rows;
    }

    [ReferenceFact]
    public async Task Six_people_creating_one_slug_at_once_make_one_company_and_no_server_error()
    {
        await using var host = await S.HostAsync();
        var clients = host.ClientsFor(S.SuperAdmin, null, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync("/superadmin/companies/")));
        string slug = $"race-{S.Tag()}";
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/superadmin/companies/",
            Form($"Race Co {i}", slug, $"race{i}.{slug}@example.com", password: S.Pw).Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)),
            tokens[i]));
        Race.NoServerErrors(rs);
        await using var db = S.Db();
        var made = await db.Companies.AsNoTracking().Where(x => x.Slug == slug).ToListAsync();
        Assert.Single(made);
        // The losers left nothing behind: one admin account, one membership.
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email.EndsWith("." + slug + "@example.com")));
        Assert.Equal(1, await db.Memberships.CountAsync(m => m.CompanyId == made[0].Id));
        Assert.True(await db.Set<AccountingPeriod>().IgnoreQueryFilters().AnyAsync(p => p.CompanyId == made[0].Id));
    }
}
