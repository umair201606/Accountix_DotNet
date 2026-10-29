using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>Helpers for the console tests (database: ACCOUNTIX_TEST_DB=superadmin).</summary>
internal static partial class S
{
    public const string Pw = "Str0ng-pass-1";

    public static string Tag() => Guid.NewGuid().ToString("N")[..10];

    public static AccountixDbContext Db(int? companyId = null)
    {
        var c = new CompanyContext();
        c.Set(companyId);
        return Reference.NewDb(c);
    }

    private static int? _super;

    /// <summary>admin@gmail.com, the seeded super admin.</summary>
    public static int SuperAdmin
    {
        get
        {
            if (_super is int id) return id;
            using var db = Db();
            _super = db.Users.AsNoTracking().Where(u => u.Email == "admin@gmail.com").Select(u => u.Id).First();
            return _super.Value;
        }
    }

    public static int Employee => Reference.UserId("emp@solarkon.com");

    public static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(SuperAdminModule).Assembly);

    /// <summary>The console plus Platform's own pages (sign-in door, portal, Settings' invite / accept), in one host.</summary>
    public static Task<ModuleHost> HostWithPlatformAsync() =>
        ModuleHost.StartAsync(typeof(SuperAdminModule).Assembly, typeof(Accountix.Platform.PlatformModule).Assembly);

    /// <summary>The antiforgery token of a page (the shell's meta tag, or a standalone page's hidden field).</summary>
    public static async Task<string> TokenAsync(PageClient c, string url)
    {
        string html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(m.Success, $"no antiforgery token on {url}");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    /// <summary>A console client: the super admin, no company chosen.</summary>
    public static PageClient Super(ModuleHost host) => host.ClientFor(SuperAdmin);

    public static async Task<int> RoleIdAsync(string name)
    {
        await using var db = Db();
        return await db.Roles.Where(r => r.Name == name).Select(r => r.Id).FirstAsync();
    }

    /// <summary>A fresh account (scrypt hash of <see cref="Pw"/>), as the Python tests' _create_user.</summary>
    public static async Task<int> NewUserAsync(string? email = null, string? fullName = null, string role = Role.Employee,
                                               bool superAdmin = false, bool allModules = true)
    {
        await using var db = Db();
        string tag = Tag();
        email ??= $"sa.{tag}@ref.test";
        var u = new User
        {
            EmployeeCode = ("SC" + tag).ToUpperInvariant(), Email = email, LoginId = email,
            PasswordHash = Accountix.Core.Security.PasswordHasher.Hash(Pw), FullName = fullName ?? email.Split('@')[0],
            RoleId = await RoleIdAsync(role), IsActive = true, IsSuperAdmin = superAdmin,
            HasHrAccess = true, HasInventoryAccess = allModules, HasInvoicingAccess = allModules, HasFinanceAccess = allModules,
            HasAccountingAccess = allModules, HasFbrAccess = allModules, HasFixedAssetsAccess = allModules,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    public static async Task<int> NewCompanyAsync(string name, string? slug = null, int? createdBy = null)
    {
        await using var db = Db();
        var c = new Company
        {
            Name = name, Slug = slug ?? $"t-{Tag()}", IsActive = true, CreatedBy = createdBy, PlanName = "free",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    public static async Task<int> AddMembershipAsync(int companyId, int userId, string role = Role.Employee,
                                                     string status = CompanyMembership.StatusActive, string? employeeCode = null)
    {
        await using var db = Db();
        var m = new CompanyMembership
        {
            CompanyId = companyId, UserId = userId, RoleId = await RoleIdAsync(role), Status = status,
            EmployeeCode = employeeCode, JoinedAt = DateTime.UtcNow,
            ModuleFlags = "{\"accounting\": false, \"executive\": false, \"fbr\": false, \"finance\": false, \"fixed_assets\": false, \"hr\": true, \"inventory\": false, \"invoicing\": false}",
        };
        db.Memberships.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    public static async Task<string> BodyAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    /// <summary>The page a redirect lands on (where the flash shows), decoded to plain text for matching.</summary>
    public static async Task<string> FollowAsync(PageClient c, HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"expected a redirect, got {(int)r.StatusCode}: {await BodyAsync(r)}");
        return Text(await (await c.GetAsync(r.Headers.Location!.OriginalString)).Content.ReadAsStringAsync());
    }

    /// <summary>HTML-decoded text (Razor writes non-ASCII as character references).</summary>
    public static string Text(string html) => WebUtility.HtmlDecode(html);

    /// <summary>POST a console form with the token of the page it lives on.</summary>
    public static Task<HttpResponseMessage> PostAsync(PageClient c, string url, string tokenPage, params (string, string)[] fields) =>
        c.PostFormAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), tokenPage);

    public static string Location(HttpResponseMessage r) => r.Headers.Location!.OriginalString;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();

    public static string Squash(string s) => Ws().Replace(s, " ");
}
