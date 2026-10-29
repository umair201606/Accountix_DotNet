using System.Text.RegularExpressions;
using Accountix.TestSupport;

namespace Accountix.Tests;

/// <summary>
/// One module's sidebar built from several projects (NAV["invoicing"]: Invoicing, Parties, Orders,
/// Returns) comes out in shared/navigation.py's order, whatever order the assemblies load in.
/// </summary>
public class NavigationTests
{
    private static async Task<List<(string Group, List<string> Items)>> SidebarAsync(params System.Reflection.Assembly[] modules)
    {
        await using var host = await ModuleHost.StartAsync(modules);
        var c = host.ClientFor(Reference.UserId(Reference.Email("owner_b")), Reference.Company("b"));
        string html = await (await c.GetAsync("/inventory/customers/")).Content.ReadAsStringAsync();
        string nav = html[html.IndexOf("<nav", StringComparison.Ordinal)..html.IndexOf("</nav>", StringComparison.Ordinal)];
        var result = new List<(string, List<string>)>();
        foreach (Match g in Regex.Matches(nav, "<div class=\"nav-group[^\"]*\" data-group=\"[^\"]*\">.*?<span>([^<]+)</span>(.*?)</div>\\s*</div>", RegexOptions.Singleline))
            result.Add((g.Groups[1].Value, Regex.Matches(g.Groups[2].Value, "<span>([^<]+)</span></a>").Select(m => m.Groups[1].Value).ToList()));
        return result;
    }

    [ReferenceFact]
    public async Task The_invoicing_sidebar_keeps_pythons_order_whatever_loads_first()
    {
        System.Reflection.Assembly[] all =
        [
            typeof(Accountix.Returns.ReturnsModule).Assembly, typeof(Accountix.Orders.OrdersModule).Assembly,
            typeof(Accountix.Parties.PartiesModule).Assembly, typeof(Accountix.Invoicing.InvoicingModule).Assembly,
            typeof(Accountix.Accounting.AccountingModule).Assembly, typeof(Accountix.Finance.FinanceModule).Assembly,
        ];
        foreach (var order in new[] { all, all.Reverse().ToArray() })
        {
            var groups = await SidebarAsync(order);
            Assert.Equal(["Procurement", "Sales", "Invoice Tracking"], groups.Select(g => g.Group).ToList());
            var proc = groups[0].Items;
            var sales = groups[1].Items;
            Assert.Equal("Suppliers", proc[0]);
            Assert.Equal("Customers", sales[0]);
            Assert.True(proc.FindIndex(i => i.Contains("Invoice") || i.Contains("PO")) < proc.IndexOf("Purchase Return"), string.Join(", ", proc));
            Assert.True(sales.FindIndex(i => i.Contains("Invoice") || i.Contains("SO")) < sales.IndexOf("Sales Return"), string.Join(", ", sales));
            Assert.Equal("Invoice Register", groups[2].Items[0]);
        }
    }

    [ReferenceFact]
    public async Task The_inventory_sidebar_has_no_financial_reports_link()
    {
        // Removed on request (29 Oct 2026): financial reports live in the Finance module only.
        await using var host = await ModuleHost.StartAsync(
            typeof(Accountix.Inventory.InventoryModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly,
            typeof(Accountix.Finance.FinanceModule).Assembly);
        var c = host.ClientFor(Reference.UserId(Reference.Email("owner_b")), Reference.Company("b"));
        string html = await (await c.GetAsync("/inventory/")).Content.ReadAsStringAsync();
        string nav = html[html.IndexOf("<nav", StringComparison.Ordinal)..html.IndexOf("</nav>", StringComparison.Ordinal)];
        Assert.DoesNotContain("Financial Reports", nav);
        Assert.DoesNotContain("href=\"/finance/\"", nav);
        Assert.Contains("Stock Valuation", nav);          // the Reports group is still there
    }
}
