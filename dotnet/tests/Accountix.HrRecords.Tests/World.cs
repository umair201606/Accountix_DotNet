using System.Text.Json;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// A fresh company in this project's database copy (ACCOUNTIX_TEST_DB=hr_records)
/// with people in given roles, built through EF as the app's own forms write
/// them: the company, accounts, memberships with this company's module
/// switches and employment details (Golden/capture/_common.py builds the same
/// through Python). Each world has its own tag, so tests repeat freely.
/// </summary>
public sealed class World
{
    public string Tag { get; } = Guid.NewGuid().ToString("N")[..8];
    public int Company { get; private set; }
    public Dictionary<string, int> Ids { get; } = new();
    public int this[string key] => Ids[key];

    public static readonly Dictionary<string, bool> HrOnly = Modules.All.ToDictionary(m => m.Key, m => m.Key == Modules.Hr);

    private static readonly Lazy<string> Hash = new(() => PasswordHasher.Hash("pw12345678"));

    public static AccountixDbContext Db(int? companyId)
    {
        var c = new CompanyContext();
        c.Set(companyId);
        return Reference.NewDb(c);
    }

    public AccountixDbContext Db() => Db(Company);

    /// <summary>people: (key, full name, role here). Created in order, with their memberships.</summary>
    public static async Task<World> CreateAsync(string name, params (string Key, string FullName, string Role)[] people)
    {
        var w = new World();
        await using var db = Db(null);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var now = DateTime.UtcNow;
        var company = new Company
        {
            Name = $"{name} {w.Tag}", Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{w.Tag}", IsActive = true, PlanName = "free",
            ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
            ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
            Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        w.Company = company.Id;
        var users = new List<(string Key, User U, string Role)>();
        foreach (var (key, fullName, role) in people)
        {
            string t = Guid.NewGuid().ToString("N")[..10];
            var u = new User
            {
                EmployeeCode = $"HR{t}".ToUpperInvariant(), Email = $"{t}@hrrec.test", LoginId = $"{t}@hrrec.test",
                PasswordHash = Hash.Value, FullName = fullName, RoleId = roles[role], IsActive = true, HasHrAccess = true,
                IsSuperAdmin = false, CreatedAt = now, UpdatedAt = now,
            };
            db.Users.Add(u);
            users.Add((key, u, role));
        }
        await db.SaveChangesAsync();
        foreach (var (key, u, role) in users)
        {
            w.Ids[key] = u.Id;
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

    /// <summary>This company's employment details on a membership.</summary>
    public async Task EmploymentAsync(string who, string? manager = null, string? department = null)
    {
        await using var db = Db(null);
        int uid = this[who];
        var m = await db.Memberships.FirstAsync(x => x.CompanyId == Company && x.UserId == uid);
        m.ManagerId = manager is null ? null : Ids[manager];
        m.Department = department;
        await db.SaveChangesAsync();
    }

    public async Task SetStatusAsync(string who, string status)
    {
        await using var db = Db(null);
        int uid = this[who];
        var m = await db.Memberships.FirstAsync(x => x.CompanyId == Company && x.UserId == uid);
        m.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>Narrow a person's rights: one row per section, as Settings → Rights writes them.</summary>
    public async Task RightsAsync(string who, params (string Section, bool View)[] rows)
    {
        await using var db = Db();
        foreach (var r in rows)
            db.UserPermissions.Add(new UserPermission
            {
                UserId = this[who], Resource = r.Section, CanView = r.View, CanCreate = r.View, CanEdit = r.View,
                CanApprove = r.View, CanDelete = r.View,
            });
        await db.SaveChangesAsync();
    }

    public PageClient Client(ModuleHost host, string who) => host.ClientFor(this[who], Company);

    public static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
