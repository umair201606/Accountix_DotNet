using System.Text.Json;

namespace Accountix.Core.Entities;

// Behaviour on the platform tables (the classes themselves are generated
// from the database in Entities/Generated). Platform tables are shared by
// every company; their queries always name the company they mean.

public partial class Role
{
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string Employee = "employee";
}

public partial class User
{
    public bool Active => IsActive != false;
    public bool SuperAdmin => IsSuperAdmin == true;
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Email : FullName;

    /// <summary>The account-wide switch: decides only outside a company (a company keeps its own on the membership).</summary>
    public bool AccountModuleFlag(string module) => module switch
    {
        Modules.Hr => HasHrAccess == true,
        Modules.Inventory => HasInventoryAccess == true,
        Modules.Invoicing => HasInvoicingAccess == true,
        Modules.Finance => HasFinanceAccess == true,
        Modules.Accounting => HasAccountingAccess == true,
        Modules.Fbr => HasFbrAccess == true,
        Modules.FixedAssets => HasFixedAssetsAccess == true,
        Modules.Executive => HasExecutiveAccess == true,
        _ => false,
    };
}

public partial class Company
{
    public bool Active => IsActive != false;

    /// <summary>Entitled to a module. NULL (a row from before the column) and unknown modules count as entitled.</summary>
    public bool ModuleEnabled(string module)
    {
        bool? value = module switch
        {
            Modules.Hr => ModHrEnabled,
            Modules.Inventory => ModInventoryEnabled,
            Modules.Invoicing => ModInvoicingEnabled,
            Modules.Finance => ModFinanceEnabled,
            Modules.Accounting => ModAccountingEnabled,
            Modules.Fbr => ModFbrEnabled,
            Modules.FixedAssets => ModFixedAssetsEnabled,
            Modules.Executive => ModExecutiveEnabled,
            _ => null,
        };
        return value != false;
    }
}

public partial class CompanyMembership
{
    public const string Pending = "pending";
    public const string StatusActive = "active";
    public const string Removed = "removed";
    public const string Blocked = "blocked";

    public bool IsActive => Status == StatusActive;

    /// <summary>This company's switch for one module (null: none stored; since Alembic 0016 every membership has them).</summary>
    public bool? ModuleFlag(string module)
    {
        if (string.IsNullOrEmpty(ModuleFlags))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(ModuleFlags);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(module, out var v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Number => v.GetDouble() != 0,
                JsonValueKind.String => v.GetString() is { Length: > 0 },
                JsonValueKind.Array => v.GetArrayLength() > 0,
                JsonValueKind.Object => v.EnumerateObject().Any(),
                _ => true,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SetModuleFlags(IReadOnlyDictionary<string, bool> flags)
    {
        // The text Python's json.dumps(..., sort_keys=True) writes.
        var sorted = new SortedDictionary<string, bool>(flags.ToDictionary(), StringComparer.Ordinal);
        ModuleFlags = JsonSerializer.Serialize(sorted).Replace(",", ", ").Replace(":", ": ");
    }
}

public partial class CompanyInvitation
{
    public const string Sent = "sent";
    public const string Accepted = "accepted";
    public const string Revoked = "revoked";
}

public partial class GlobalLimit
{
    public int MemberLimitFor(Company company) => company.MaxMembers ?? DefaultMaxMembers;
    public int CompanyLimitFor(User user) => user.MaxCompaniesOwned ?? MaxCompaniesPerUser;
    public int? JoinLimitFor(User user) => user.MaxCompaniesJoined;
}

public partial class UserPermission
{
    public bool Allows(string action) => action switch
    {
        Rights.View => CanView == true,
        Rights.Create => CanCreate == true,
        Rights.Edit => CanEdit == true,
        Rights.Approve => CanApprove == true,
        Rights.Delete => CanDelete == true,
        _ => false,
    };
}
