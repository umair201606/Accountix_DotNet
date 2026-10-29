using Accountix.E2E.Harness;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Modules;

/// <summary>
/// Every parameter-free page of each module marked done, at 1366, 375, 360 and 320 px,
/// in the integrated app (every module of this build, as the real host loads them):
/// it loads, nothing errors in the console or on the server, nothing is cut off or
/// scrolls sideways (tests/e2e/test_responsive_pages.py, for every page). Signed in as
/// the super admin in company 1 "Default" (the seeded demo data), as the Python suite;
/// employee self-service also as the employee.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class ModulePagesTests(FullApp app) : IClassFixture<FullApp>
{
    private const string Admin = "admin", Employee = "employee", Anonymous = "anonymous";

    private static readonly Dictionary<string, (string Who, string Path)[]> Pages = new()
    {
        ["Hr"] =
        [
            (Admin, "/dashboard"), (Admin, "/attendance/"), (Admin, "/attendance/admin"), (Admin, "/attendance/overview"),
            (Admin, "/attendance/policies"), (Admin, "/leaves/"), (Admin, "/leaves/apply"), (Admin, "/leaves/approvals"),
            (Admin, "/leaves/calendar"), (Admin, "/leaves/holidays"), (Admin, "/leaves/workflows"), (Admin, "/timesheets/"),
            (Admin, "/timesheets/approvals"), (Admin, "/timesheets/projects"), (Admin, "/ess/"), (Admin, "/ess/change-requests"),
            (Admin, "/ess/loans"), (Admin, "/ess/performance"), (Admin, "/ess/slips"), (Admin, "/mss/"), (Admin, "/mss/approvals"),
            (Admin, "/mss/team"), (Admin, "/mss/team-availability"), (Admin, "/auth/users"), (Admin, "/auth/members/assign"),
            (Admin, "/auth/profile"), (Admin, "/auth/change-password"),
            (Employee, "/dashboard"), (Employee, "/attendance/"), (Employee, "/leaves/"), (Employee, "/leaves/apply"),
            (Employee, "/ess/"), (Employee, "/ess/loans"), (Employee, "/auth/profile"),
        ],
        ["FixedAssets"] =
        [
            (Admin, "/fixed-assets/"), (Admin, "/fixed-assets/dashboard"), (Admin, "/fixed-assets/assets/"),
            (Admin, "/fixed-assets/assets/create"), (Admin, "/fixed-assets/categories/"), (Admin, "/fixed-assets/categories/create"),
            (Admin, "/fixed-assets/depreciation/"), (Admin, "/fixed-assets/reports/"), (Admin, "/fixed-assets/reports/movement"),
            (Admin, "/fixed-assets/transfers/"), (Admin, "/fixed-assets/transfers/create"),
            (Admin, "/inventory/transfer-to-fa/"), (Admin, "/inventory/transfer-to-fa/create"),
        ],
        ["Fbr"] = [(Admin, "/fbr/"), (Admin, "/fbr/settings/")],
        ["Invoicing"] =
        [
            (Admin, "/invoicing/"), (Admin, "/inventory/invoices/"), (Admin, "/inventory/invoices/list"),
            (Admin, "/inventory/purchase-invoice/"), (Admin, "/inventory/purchase-invoice/list"),
        ],
        ["Orders"] =
        [
            (Admin, "/inventory/sales/"), (Admin, "/inventory/sales/list"),
            (Admin, "/inventory/purchases/"), (Admin, "/inventory/purchases/list"),
        ],
        ["Inventory"] =
        [
            (Admin, "/inventory/"), (Admin, "/inventory/dashboard"), (Admin, "/inventory/products/"), (Admin, "/inventory/products/create"),
            (Admin, "/inventory/products/batch"), (Admin, "/inventory/categories/"), (Admin, "/inventory/categories/create"),
            (Admin, "/inventory/units/"), (Admin, "/inventory/units/create"), (Admin, "/inventory/units/batch"),
            (Admin, "/inventory/stock/"), (Admin, "/inventory/stock/movements"),
            (Admin, "/inventory/vouchers/consumption"), (Admin, "/inventory/vouchers/consumption/list"),
            (Admin, "/inventory/vouchers/scrap"), (Admin, "/inventory/vouchers/scrap/list"),
            (Admin, "/inventory/vouchers/adjustment"), (Admin, "/inventory/vouchers/adjustment/list"),
            (Admin, "/inventory/vouchers/stock-take"), (Admin, "/inventory/vouchers/stock-take/list"),
            (Admin, "/inventory/vouchers/product-ledger"), (Admin, "/inventory/vouchers/product-ledger/list"),
            (Admin, "/inventory/reports/low-stock"), (Admin, "/inventory/reports/stock-ledger"), (Admin, "/inventory/reports/valuation"),
            (Admin, "/inventory/reports/valuation?as_of=2026-12-31&view=1"),
        ],
        ["Finance"] =
        [
            (Admin, "/finance/"), (Admin, "/finance/trial-balance"), (Admin, "/finance/profit-loss"), (Admin, "/finance/balance-sheet"),
            (Admin, "/finance/socie"), (Admin, "/finance/cash-flow"), (Admin, "/finance/ledger"), (Admin, "/finance/label-pl"),
            (Admin, "/finance/twcf"),
            (Admin, "/finance/trial-balance?filter_mode=custom&from=2026-01-01&to=2026-12-31"),
            (Admin, "/finance/profit-loss?filter_mode=custom&from=2026-01-01&to=2026-12-31"),
            (Admin, "/finance/balance-sheet?filter_mode=custom&from=2026-01-01&to=2026-12-31"),
            (Admin, "/finance/socie?filter_mode=custom&from=2026-01-01&to=2026-12-31"),
            (Admin, "/finance/cash-flow?filter_mode=custom&from=2026-01-01&to=2026-12-31"),
            (Admin, "/finance/ledger?filter_mode=custom&from=2026-01-01&to=2026-12-31&mode=all"),
            (Admin, "/finance/label-pl?filter_mode=custom&from=2026-01-01&to=2026-12-31"),
        ],
        ["Platform"] =
        [
            (Anonymous, "/"), (Anonymous, "/auth/login"), (Anonymous, "/signup"),
            (Admin, "/portal/"), (Admin, "/dashboard/"), (Employee, "/dashboard/"), (Employee, "/portal/"),
            (Admin, "/settings/"), (Admin, "/settings/?tab=account"), (Admin, "/settings/?tab=company"),
            (Admin, "/settings/?tab=periods"), (Admin, "/settings/?tab=reports"), (Admin, "/settings/?tab=cash_flow"),
            (Admin, "/settings/?tab=labels"), (Admin, "/settings/?tab=inventory"), (Admin, "/settings/?tab=purchase"),
            (Admin, "/settings/?tab=sales"), (Admin, "/settings/?tab=invoicing"), (Admin, "/settings/?tab=templates"),
            (Admin, "/settings/?tab=rights"), (Admin, "/settings/?tab=members"), (Admin, "/settings/?tab=invites"),
            (Admin, "/settings/?tab=audit"), (Admin, "/settings/members/"), (Admin, "/settings/invitations/"),
            (Admin, "/settings/labels"), (Employee, "/settings/"),
        ],
        ["Executive"] =
        [
            (Admin, "/executive/"), (Admin, "/executive/integrity"), (Admin, "/executive/receivables"),
            (Admin, "/executive/payables"), (Admin, "/executive/settings"),
        ],
        ["Payroll"] =
        [
            (Admin, "/compensation/"), (Admin, "/compensation/create-profile"), (Admin, "/compensation/loans"),
            (Admin, "/compensation/revisions"), (Admin, "/compensation/run-payroll"), (Admin, "/compensation/tax-settings"),
            (Admin, "/pf/"), (Admin, "/pf/config"),
        ],
        ["HrRecords"] =
        [
            (Admin, "/communications/"), (Admin, "/digital-files/"), (Admin, "/digital-files/admin"),
            (Admin, "/workplace/"), (Admin, "/workplace/announcements"), (Admin, "/workplace/events"), (Admin, "/workplace/kanban"),
            (Admin, "/reports/"), (Employee, "/communications/"), (Employee, "/digital-files/"), (Employee, "/workplace/"),
        ],
        ["Returns"] =
        [
            (Admin, "/inventory/purchase-return/"), (Admin, "/inventory/purchase-return/list"),
            (Admin, "/invoicing/sales-return/"), (Admin, "/invoicing/sales-return/list"),
        ],
        ["Parties"] =
        [
            (Admin, "/inventory/customers/"), (Admin, "/inventory/customers/create"), (Admin, "/inventory/customers/batch"),
            (Admin, "/inventory/suppliers/"), (Admin, "/inventory/suppliers/create"), (Admin, "/inventory/suppliers/batch"),
            (Admin, "/invoicing/tracking/"), (Admin, "/invoicing/tracking/sales"), (Admin, "/invoicing/tracking/purchases"),
            (Admin, "/invoicing/registers/"),
            (Admin, "/invoicing/registers/?doc=sales&mode=custom&from=2020-01-01&to=2030-01-01&view=1"),
            (Admin, "/invoicing/registers/?doc=purchase&mode=year&view=1"),
        ],
    };

    private static TheoryData<string, string, string> CasesFor(string module)
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (who, path) in Pages[module])
            foreach (var vp in Viewport.All)
                if (KnownIssues.For($"{path}:{vp.Name}") is null)
                    data.Add(who, path, vp.Name);
        return data;
    }

    public static TheoryData<string, string, string> Hr() => CasesFor("Hr");
    public static TheoryData<string, string, string> FixedAssets() => CasesFor("FixedAssets");
    public static TheoryData<string, string, string> Fbr() => CasesFor("Fbr");
    public static TheoryData<string, string, string> Invoicing() => CasesFor("Invoicing");
    public static TheoryData<string, string, string> Parties() => CasesFor("Parties");
    public static TheoryData<string, string, string> Orders() => CasesFor("Orders");
    public static TheoryData<string, string, string> Inventory() => CasesFor("Inventory");
    public static TheoryData<string, string, string> Finance() => CasesFor("Finance");
    public static TheoryData<string, string, string> Platform() => CasesFor("Platform");
    public static TheoryData<string, string, string> Executive() => CasesFor("Executive");
    public static TheoryData<string, string, string> Payroll() => CasesFor("Payroll");
    public static TheoryData<string, string, string> HrRecords() => CasesFor("HrRecords");
    public static TheoryData<string, string, string> Returns() => CasesFor("Returns");

    /// <summary>Cases blocked by an open finding (finding id, who, path, width).</summary>
    public static TheoryData<string, string, string, string> Blocked()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (_, pages) in Pages)
            foreach (var (who, path) in pages)
                foreach (var vp in Viewport.All)
                    if (KnownIssues.For($"{path}:{vp.Name}") is { } f)
                        data.Add(f, who, path, vp.Name);
        if (data.Count == 0)
            data.Add("none", "", "", "");
        return data;
    }

    [E2ETheory("Hr")]
    [MemberData(nameof(Hr))]
    public Task Hr_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("FixedAssets")]
    [MemberData(nameof(FixedAssets))]
    public Task Fixed_assets_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Fbr")]
    [MemberData(nameof(Fbr))]
    public Task Fbr_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Invoicing")]
    [MemberData(nameof(Invoicing))]
    public Task Invoicing_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Parties")]
    [MemberData(nameof(Parties))]
    public Task Parties_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Orders")]
    [MemberData(nameof(Orders))]
    public Task Orders_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Inventory")]
    [MemberData(nameof(Inventory))]
    public Task Inventory_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Finance")]
    [MemberData(nameof(Finance))]
    public Task Finance_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Platform")]
    [MemberData(nameof(Platform))]
    public Task Platform_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Executive")]
    [MemberData(nameof(Executive))]
    public Task Executive_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Payroll")]
    [MemberData(nameof(Payroll))]
    public Task Payroll_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("HrRecords")]
    [MemberData(nameof(HrRecords))]
    public Task Hr_records_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [E2ETheory("Returns")]
    [MemberData(nameof(Returns))]
    public Task Returns_page(string who, string path, string viewport) => CheckAsync(who, path, viewport);

    [KnownIssueTheory("Hr", "FixedAssets", "Fbr", "Invoicing", "Parties")]
    [MemberData(nameof(Blocked))]
    public Task Blocked_page(string finding, string who, string path, string viewport) =>
        finding == "none" ? Task.CompletedTask : CheckAsync(who, path, viewport);

    private async Task CheckAsync(string who, string path, string viewport)
    {
        int? user = who switch { Employee => Reference.EmployeeId, Anonymous => null, _ => E2EEnv.SuperAdmin };
        string tag = "mod_" + new string(path.Where(char.IsLetterOrDigit).Take(40).ToArray()) + "_" + who;
        await using var s = await app.SessionAsync(Viewport.Named(viewport), user, user is null ? null : E2EEnv.DefaultCompany, test: tag);
        await s.GoAsync(path);
        await Expect(s.Page.Locator("body")).Not.ToBeEmptyAsync();
        await s.AssertCleanAsync();
    }

    private static class Reference
    {
        public static int EmployeeId => TestSupport.Reference.UserId("emp@solarkon.com");
    }
}
