using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// A fresh company in the HR test database with people in given roles, the
/// way the Python E2E tests build theirs (a company, accounts, memberships
/// with this company's own module switches and employment details). Every
/// world has its own tag, so tests repeat without clashing.
/// </summary>
public sealed class World
{
    public string Tag { get; } = Guid.NewGuid().ToString("N")[..8];
    public int Company { get; private set; }
    public Dictionary<string, int> Ids { get; } = new();
    public int this[string key] => Ids[key];

    public static readonly Dictionary<string, bool> HrOnly = Modules.All.ToDictionary(m => m.Key, m => m.Key == Modules.Hr);

    public static AccountixDbContext Db(int? companyId)
    {
        var c = new CompanyContext();
        c.Set(companyId);
        return Reference.NewDb(c);
    }

    public AccountixDbContext Db() => Db(Company);

    /// <summary>people: (key, full name, role here, global role). Created in order; memberships too.</summary>
    public static async Task<World> CreateAsync(string name, params (string Key, string FullName, string Role)[] people)
    {
        var w = new World();
        await using var db = Db(null);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var company = new Company
        {
            Name = $"{name} {w.Tag}", Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{w.Tag}", IsActive = true, PlanName = "free",
            ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
            ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
            Timezone = "Asia/Karachi", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        w.Company = company.Id;
        string hash = PasswordHasher.Hash("pw12345678");
        foreach (var (key, fullName, role) in people)
        {
            var u = await w.AddUserAsync(db, roles, key, fullName, role, hash);
            db.Memberships.Add(NewMembership(company.Id, u.Id, roles[role]));
        }
        await db.SaveChangesAsync();
        return w;
    }

    public static CompanyMembership NewMembership(int companyId, int userId, int roleId, string status = CompanyMembership.StatusActive)
    {
        var m = new CompanyMembership { CompanyId = companyId, UserId = userId, RoleId = roleId, Status = status, JoinedAt = DateTime.UtcNow };
        m.SetModuleFlags(HrOnly);
        return m;
    }

    private async Task<User> AddUserAsync(AccountixDbContext db, Dictionary<string, int> roles, string key, string fullName, string role, string hash)
    {
        string t = Guid.NewGuid().ToString("N")[..8];
        var u = new User
        {
            EmployeeCode = $"HT{t}".ToUpperInvariant(), Email = $"{t}@hr.test", LoginId = $"{t}@hr.test", PasswordHash = hash,
            FullName = fullName, RoleId = roles[role], IsActive = true, HasHrAccess = true, IsSuperAdmin = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        Ids[key] = u.Id;
        return u;
    }

    /// <summary>A person with no membership here (the Python tests' "outsider").</summary>
    public async Task<int> OutsiderAsync(string key, string fullName, string role = Role.Manager)
    {
        await using var db = Db(null);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var u = await AddUserAsync(db, roles, key, fullName, role, PasswordHasher.Hash("pw12345678"));
        return u.Id;
    }

    /// <summary>Set this company's employment details on a membership (manager etc.).</summary>
    public async Task SetManagerAsync(string who, string? manager)
    {
        await using var db = Db(null);
        int uid = this[who];
        var m = await db.Memberships.FirstAsync(x => x.CompanyId == Company && x.UserId == uid);
        m.ManagerId = manager is null ? null : Ids[manager];
        await db.SaveChangesAsync();
    }

    public async Task<int> LeaveTypeAsync(string name, string code, int quota)
    {
        await using var db = Db();
        var lt = new LeaveType { Name = name, Code = code, DefaultQuota = quota, IsPaid = true, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 };
        db.Add(lt);
        await db.SaveChangesAsync();
        return lt.Id;
    }

    /// <summary>Narrow a person's rights: one row per section, as Settings → Rights writes them.</summary>
    public async Task RightsAsync(string who, params (string Section, bool View, bool Create, bool Edit, bool Approve, bool Delete)[] rows)
    {
        await using var db = Db();
        foreach (var r in rows)
            db.UserPermissions.Add(new UserPermission
            {
                UserId = this[who], Resource = r.Section, CanView = r.View, CanCreate = r.Create, CanEdit = r.Edit,
                CanApprove = r.Approve, CanDelete = r.Delete,
            });
        await db.SaveChangesAsync();
    }

    public PageClient Client(ModuleHost host, string who) => host.ClientFor(this[who], Company);

    /// <summary>Make an existing person (maybe another world's) a member here.</summary>
    public async Task JoinAsync(string key, int userId, string role, string status = CompanyMembership.StatusActive)
    {
        await using var db = Db(null);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        db.Memberships.Add(NewMembership(Company, userId, roles[role], status));
        await db.SaveChangesAsync();
        Ids[key] = userId;
    }

    public async Task<CompanyMembership> MembershipAsync(string who)
    {
        await using var db = Db(null);
        int uid = this[who];
        return await db.Memberships.AsNoTracking().FirstAsync(m => m.CompanyId == Company && m.UserId == uid);
    }

    public async Task<User> UserAsync(string who)
    {
        await using var db = Db(null);
        int uid = this[who];
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == uid);
    }

    /// <summary>POST a form with the token from a page this person can open.</summary>
    public static Task<HttpResponseMessage> PostAsync(PageClient c, string url, string tokenPage, params (string Key, string Value)[] fields) =>
        c.PostFormAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)), tokenPage);

    /// <summary>The same, asking for JSON (Accept: application/json), as a script would.</summary>
    public static async Task<HttpResponseMessage> PostJsonAcceptAsync(PageClient c, string url, string tokenPage, params (string Key, string Value)[] fields)
    {
        string token = await c.TokenFromAsync(tokenPage);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new("__RequestVerificationToken", token))),
        };
        req.Headers.Accept.ParseAdd("application/json");
        return await c.SendAsync(req);
    }
}
