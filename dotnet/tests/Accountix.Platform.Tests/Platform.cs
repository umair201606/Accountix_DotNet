using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Platform.Tests;

/// <summary>Helpers for the Platform tests (database: ACCOUNTIX_TEST_DB=platform).</summary>
internal static class P
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int OwnerA => Reference.UserId(Reference.Email("owner_a"));
    public static int Restricted => Reference.UserId(Reference.Email("restricted"));
    public static int Blocked => Reference.UserId(Reference.Email("blocked"));
    public static int CompanyA => Reference.Company("a");
    public static int CompanyB => Reference.Company("b");
    public static string Password => Reference.Json.GetProperty("password").GetString()!;

    public static string Tag() => Guid.NewGuid().ToString("N")[..8];

    public static AccountixDbContext Db(int? companyId = null)
    {
        var c = new CompanyContext();
        c.Set(companyId);
        return Reference.NewDb(c);
    }

    public static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(PlatformModule).Assembly);

    /// <summary>Platform with Accounting (voucher form, ledger, period lock), for tests that post.</summary>
    public static Task<ModuleHost> HostWithAccountingAsync() =>
        ModuleHost.StartAsync(typeof(PlatformModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    /// <summary>A scope of the host's services (a context WITH the app's interceptors: audit, query counter).</summary>
    public static (IServiceScope Scope, AccountixDbContext Db) Scope(ModuleHost host, int? companyId = null)
    {
        var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountixDbContext>();
        db.Company.Set(companyId);
        return (scope, db);
    }

    /// <summary>A fresh, provisioned company with a fresh admin (its owner): (company, admin).</summary>
    public static async Task<(int Company, int Admin)> FreshCompanyAsync(ModuleHost host, bool provision = true)
    {
        int admin = await NewUserAsync();
        var (scope, db) = Scope(host);
        using (scope)
        {
            string tag = Tag();
            var c = new Company { Name = "Fresh " + tag, Slug = "fresh-" + tag, IsActive = true, PlanName = "free", CreatedBy = admin, CreatedAt = DateTime.UtcNow };
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            int roleId = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => r.Id).FirstAsync();
            await scope.ServiceProvider.GetRequiredService<Accountix.Platform.Services.Memberships>().AddAsync(c.Id, admin, roleId);
            if (provision)
                await scope.ServiceProvider.GetRequiredService<Accountix.Platform.Provisioning.CompanySetup>().ProvisionAsync(c.Id);
            return (c.Id, admin);
        }
    }

    /// <summary>A new person added to a company in a role (a further company when they already have one).</summary>
    public static async Task<int> AddMemberAsync(ModuleHost host, int companyId, string role, int? userId = null)
    {
        int uid = userId ?? await NewUserAsync();
        var (scope, db) = Scope(host);
        using (scope)
        {
            int roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).FirstAsync();
            await scope.ServiceProvider.GetRequiredService<Accountix.Platform.Services.Memberships>().AddAsync(companyId, uid, roleId);
        }
        return uid;
    }

    /// <summary>The antiforgery token of a page (the shell's meta tag, or a standalone page's hidden field).</summary>
    public static Task<string> TokenAsync(PageClient c, string url) => AuthPortalTests.FormTokenAsync(c, url);

    /// <summary>POST a form with the token of <paramref name="tokenPage"/>.</summary>
    public static async Task<HttpResponseMessage> PostAsync(PageClient c, string url, string tokenPage, params (string, string)[] fields) =>
        await c.PostFormWithTokenAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), await TokenAsync(c, tokenPage));

    /// <summary>The flash messages carried to the next page.</summary>
    public static async Task<string> FollowAsync(PageClient c, HttpResponseMessage r)
    {
        Assert.True(r.Headers.Location is not null, $"{(int)r.StatusCode}: {(await r.Content.ReadAsStringAsync())[..Math.Min(3000, (int)(r.Content.Headers.ContentLength ?? 3000))]}");
        return System.Net.WebUtility.HtmlDecode(await (await c.GetAsync(r.Headers.Location!.OriginalString)).Content.ReadAsStringAsync());
    }

    /// <summary>The 8-character tag of a company made by <see cref="FreshCompanyAsync"/> or a test ("fresh-1a2b3c4d" → "1a2b3c4d").</summary>
    public static async Task<string> TagOfAsync(int companyId)
    {
        await using var db = Db();
        string slug = await db.Companies.Where(c => c.Id == companyId).Select(c => c.Slug).SingleAsync();
        return slug[(slug.LastIndexOf('-') + 1)..];
    }

    /// <summary>
    /// Makes an answer comparable with the golden one captured from Python on another company:
    /// the run's random tag → "TAG", each named id in a JSON field ("created_by": 17) → its role,
    /// and, with <paramref name="years"/>, years near today → "Y+0", "Y-1", ... (for data made relative to
    /// today, such as the periods provisioning makes around the current year; fixed dates stay literal).
    /// </summary>
    public static string Norm(string s, string? tag = null, IReadOnlyDictionary<int, string>? ids = null, bool years = false)
    {
        if (tag is { Length: > 0 })
            s = s.Replace(tag, "TAG");
        if (ids is not null)
            foreach (var (id, role) in ids)
                s = System.Text.RegularExpressions.Regex.Replace(s, $"(\"[a-z_]+\": ){id}(?![0-9])", "${1}\"" + role + "\"");
        if (!years)
            return s;
        int year = DateTime.Now.Year;
        return System.Text.RegularExpressions.Regex.Replace(s, "(?<![0-9])20[0-9]{2}(?![0-9])", m =>
        {
            int d = int.Parse(m.Value) - year;
            return Math.Abs(d) <= 10 ? (d < 0 ? $"Y{d}" : $"Y+{d}") : m.Value;
        });
    }

    /// <summary>A fresh user (scrypt hash of the reference password), returned id.</summary>
    public static async Task<int> NewUserAsync(string? email = null, bool superAdmin = false, string? fullName = null)
    {
        await using var db = Db();
        string tag = Tag();
        var role = await db.Roles.FirstAsync(r => r.Name == Role.Employee);
        var u = new User
        {
            EmployeeCode = "T" + tag, Email = email ?? $"t.{tag}@ref.test", LoginId = email ?? $"t.{tag}@ref.test",
            PasswordHash = Accountix.Core.Security.PasswordHasher.Hash(Password), FullName = fullName ?? $"Test {tag}",
            RoleId = role.Id, IsActive = true, IsSuperAdmin = superAdmin, HasHrAccess = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }
}
