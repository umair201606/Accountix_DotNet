using Accountix.TestSupport;

namespace Accountix.Tests;

/// <summary>The app shell around every module page.</summary>
public class ShellTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int CompanyB => Reference.Company("b");

    [ReferenceFact]
    public async Task The_notification_bell_is_in_the_shell_when_hr_records_is_loaded()
    {
        await using var with = await ModuleHost.StartAsync(
            typeof(Accountix.Accounting.AccountingModule).Assembly, typeof(Accountix.HrRecords.HrRecordsModule).Assembly);
        string html = await (await with.ClientFor(OwnerB, CompanyB).GetAsync("/accounting/")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"notif-btn\"", html);
        Assert.Contains("id=\"mark-all-read\"", html);

        // Without the module the page still renders, just without the bell.
        await using var without = await ModuleHost.StartAsync(typeof(Accountix.Accounting.AccountingModule).Assembly);
        var r = await without.ClientFor(OwnerB, CompanyB).GetAsync("/accounting/");
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain("id=\"notif-btn\"", await r.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task Pages_keep_non_ascii_text_as_utf8_like_jinja()
    {
        await using var host = await ModuleHost.StartAsync(typeof(Accountix.Accounting.AccountingModule).Assembly);
        string html = await (await host.ClientFor(OwnerB, CompanyB).GetAsync("/accounting/coa/")).Content.ReadAsStringAsync();
        Assert.Contains("—", html);                 // e.g. "Other Expenses — General"
        Assert.DoesNotContain("&#x2014;", html);
        Assert.DoesNotContain("<script>alert", html);
    }

    [ReferenceFact]
    public async Task A_module_refusal_renders_in_the_app_shell_with_pythons_words()
    {
        // E2E finding F8: the page took its layout from the web host's _ViewStart only, so module hosts showed it bare.
        await using var host = await ModuleHost.StartAsync(typeof(Accountix.FixedAssets.FixedAssetsModule).Assembly);
        int restricted = Reference.UserId(Reference.Email("restricted"));     // a manager in company B without fixed assets
        var r = await host.ClientFor(restricted, CompanyB).GetAsync("/fixed-assets/");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("class=\"sidebar\"", html);
        Assert.Contains("Module Not Available", html);
        Assert.Contains("This module is not available to you in this company.", html);
        Assert.Contains("href=\"/dashboard/\"", html);
    }
}
