using Accountix.Core.Entities;
using Accountix.Platform.Provisioning;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Platform.Tests;

/// <summary>
/// A provisioned company gets the chart, numbering, settings and audit rows the Python app gave a
/// company it provisioned (golden: Golden/Provisioning.json, captured 29 Oct 2026).
/// </summary>
public class ProvisioningTests
{
    private static async Task<int> NetCompanyAsync(ModuleHost host, string slug)
    {
        var (scope, db) = P.Scope(host);
        using (scope)
        {
            var c = new Company { Name = "Prov " + slug, Slug = slug, IsActive = true, PlanName = "free", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<CompanySetup>().ProvisionAsync(c.Id);
            return c.Id;
        }
    }

    /// <summary>A row's columns as text, minus ids and timestamps (they differ between two companies).</summary>
    internal static string Row(Microsoft.EntityFrameworkCore.DbContext db, object e, params string[] skip)
    {
        var et = db.Model.FindEntityType(e.GetType())!;
        var parts = new List<string>();
        foreach (var p in et.GetProperties())
        {
            string col = p.GetColumnName();
            if (col is "id" or "company_id" or "created_at" or "updated_at" || skip.Contains(col)) continue;
            object? v = p.PropertyInfo!.GetValue(e);
            parts.Add(col + "=" + (v is decimal d ? d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return string.Join("|", parts);
    }

    /// <summary>Everything provisioning wrote for a company, ids replaced by codes.</summary>
    internal static async Task<Dictionary<string, List<string>>> ShapeAsync(int cid)
    {
        await using var db = P.Db(cid);
        var accounts = await db.Set<ChartOfAccount>().AsNoTracking().OrderBy(a => a.Code).ToListAsync();
        var codeOf = accounts.ToDictionary(a => a.Id, a => a.Code);
        string Pid(int? id) => id is int i && codeOf.TryGetValue(i, out var c) ? c : "";
        async Task<List<string>> All<T>(params string[] skip) where T : class =>
            (await db.Set<T>().AsNoTracking().OrderBy(e => EF.Property<int>(e, "Id")).ToListAsync()).Select(e => Row(db, e, skip)).ToList();
        return new Dictionary<string, List<string>>
        {
            ["chart"] = accounts.Select(a => Row(db, a, "parent_id") + "|parent=" + Pid(a.ParentId)).ToList(),
            ["numbers"] = (await db.Set<VoucherNumber>().AsNoTracking().OrderBy(v => v.Prefix).ToListAsync()).Select(v => Row(db, v)).ToList(),
            ["fa_categories"] = await All<FaCategory>(),
            ["periods"] = await All<AccountingPeriod>(),
            ["company_info"] = await All<CompanyInfo>(),
            ["fiscal_year_rule"] = await All<FiscalYearRule>(),
            ["report_settings"] = await All<ReportSetting>(),
            ["inventory_settings"] = await All<InventorySetting>(),
            ["invoice_templates"] = await All<InvoiceTemplate>(),
        };
    }

    /// <summary>The audit rows of a company's provisioning, ids made comparable (company → C, account ids → codes).</summary>
    internal static async Task<List<string>> AuditAsync(int cid)
    {
        await using var db = P.Db(cid);
        var codeOf = await db.Set<ChartOfAccount>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == cid).OrderBy(a => a.Id).ToListAsync();
        return rows.Select(r =>
        {
            string changes = r.Changes ?? "";
            changes = System.Text.RegularExpressions.Regex.Replace(changes, "\"company_id\": \\d+", "\"company_id\": C");
            changes = System.Text.RegularExpressions.Regex.Replace(changes, "\"parent_id\": (\\d+)",
                m => "\"parent_id\": " + codeOf.GetValueOrDefault(int.Parse(m.Groups[1].Value), "?"));
            string? reference = r.Reference, summary = r.Summary;
            if (reference is not null && reference == r.EntityId?.ToString())
            {
                summary = summary?.Replace(" " + reference + " ", " ID ");
                reference = "ID";
            }
            return $"{r.Action}|{r.Module}|{r.EntityType}|{reference}|{summary}|{changes}";
        }).ToList();
    }

    /// <summary>Provisioning's rows and audit trail, normalised (tag → TAG, years relative to this year).</summary>
    private static async Task<Dictionary<string, List<string>>> GoldenShapeAsync(int cid, string tag)
    {
        var shape = await ShapeAsync(cid);
        shape["audit"] = await AuditAsync(cid);
        return shape.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => P.Norm(v, tag, years: true)).ToList());
    }

    [ReferenceFact]
    public async Task A_provisioned_company_gets_what_python_provisioned()
    {
        await using var host = await P.HostAsync();
        string tag = P.Tag();
        int net = await NetCompanyAsync(host, "prov-" + tag);
        var shape = await GoldenShapeAsync(net, tag);
        Assert.Equal(161, shape["chart"].Count);
        Assert.Equal(14, shape["numbers"].Count);
        Assert.Equal(8, shape["periods"].Count);                     // this year − 5 .. this year + 2
        Assert.Equal(4, shape["invoice_templates"].Count);
        Golden.Match("Provisioning", shape);
    }
}
