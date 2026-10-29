using System.Globalization;
using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

public sealed class RunHarness : Harness
{
    protected override Task<World> BuildAsync() => World.CreateAsync("Payroll Run Co", [new World.Person("Nida New", "2020-01-01")]);

    protected override Task SeedAsync() => W.ProfileAsync(W.Admin, 50_000, "2000-01-01",
        ("Conveyance", "allowance", "fixed", 5000m, true), ("EOBI", "deduction", "fixed", 1000m, true));
}

/// <summary>
/// tests/e2e/test_payroll_run.py through the .NET routes: the journal a run posts
/// (a deduction component, a custom deduction, a loan instalment), the run page,
/// the accounting-entry panel, the slip and the new-profile defaults.
/// </summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed class PayrollRunTests(RunHarness h) : IClassFixture<RunHarness>
{
    private World W => h.W;
    private static readonly DateOnly Prev = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
    private static readonly DateOnly Before = new DateOnly(Prev.Year, Prev.Month, 1).AddDays(-1);
    private static readonly DateOnly Third = new DateOnly(Before.Year, Before.Month, 1).AddDays(-1);

    private async Task<Dictionary<string, decimal>> CreditsAsync(int runId) =>
        (await W.VoucherLinesAsync("PRL", runId)).ToDictionary(kv => kv.Key, kv => -kv.Value);

    [ReferenceFact]
    public async Task S01_component_deduction_posts_a_balanced_journal()
    {
        var r = await h.PostAsync("/compensation/run-payroll", ("month", Prev.Month.ToString()), ("year", Prev.Year.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var run = await W.RunAsync(Prev.Month, Prev.Year);
        Assert.NotNull(run);
        Assert.Equal(55_000m, run.TotalGross);
        var cr = await CreditsAsync(run.Id);
        Assert.Equal(run.TotalNet, cr["Salary Payable"]);
        Assert.Equal(1000m, cr["Other Payroll Deductions Payable"]);
        Assert.Equal(0m, cr.Values.Sum());
        await W.AssertBooksTieAsync("payroll with a component deduction");
    }

    [ReferenceFact]
    public async Task S02_custom_deduction_is_not_owed_to_staff()
    {
        var adj = new Dictionary<string, object> { [W.Admin.ToString()] = new Dictionary<string, string> { ["custom_deduction"] = "750" } };
        var r = await h.PostAsync("/compensation/run-payroll", ("month", Before.Month.ToString()), ("year", Before.Year.ToString()),
                                  ("adjustments", JsonSerializer.Serialize(adj)));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var run = (await W.RunAsync(Before.Month, Before.Year))!;
        var cr = await CreditsAsync(run.Id);
        Assert.Equal(run.TotalNet, cr["Salary Payable"]);
        Assert.Equal(1750m, cr["Other Payroll Deductions Payable"]);
        Assert.Equal(run.TotalNet, run.TotalGross - run.TotalDeductions);
        await W.AssertBooksTieAsync("payroll with a custom deduction");
    }

    [ReferenceFact]
    public async Task S03_run_detail_page()
    {
        var run = (await W.RunAsync(Prev.Month, Prev.Year))!;
        var r = await h.C.GetAsync($"/compensation/run/{run.Id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        await using var db = W.Db();
        string name = (await db.Users.FirstAsync(u => u.Id == W.Admin)).FullName;
        Assert.Contains(name, html);
        Assert.Contains(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(Prev.Month), html);
        Assert.Contains(Prev.Year.ToString(), html);
        Assert.Contains("data-type=\"PRL\"", html);
        Assert.Contains($"data-id=\"{run.Id}\"", html);
        Assert.Contains("55,000", html);
        Assert.Contains("/compensation/slip/", html);
        Assert.Contains($"/compensation/export-bank-ledger/{run.Id}", html);
        Assert.Contains(W.Codes[0], html);
    }

    [ReferenceFact]
    public async Task S04_history_links_to_the_run()
    {
        var run = (await W.RunAsync(Prev.Month, Prev.Year))!;
        string html = await (await h.C.GetAsync("/compensation/")).Content.ReadAsStringAsync();
        Assert.Contains($"/compensation/run/{run.Id}", html);
    }

    [ReferenceFact]
    public async Task S05_accounting_entry_panel_shows_the_payroll_journal()
    {
        var run = (await W.RunAsync(Prev.Month, Prev.Year))!;
        var body = JsonDocument.Parse(await (await h.C.GetAsync($"/accounting/gl-entry?type=PRL&id={run.Id}")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("ok").GetBoolean());
        var main = body.GetProperty("posted")[0];
        Assert.Equal("Payroll", main.GetProperty("title").GetString());
        Assert.True(main.GetProperty("balanced").GetBoolean());
        var names = main.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "Salary Expense", "Salary Payable", "Other Payroll Deductions Payable" }, names);
    }

    [ReferenceFact]
    public async Task S06_unknown_run_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await h.C.GetAsync("/compensation/run/999999")).StatusCode);

    [ReferenceFact]
    public async Task S07_slip_shows_this_company_not_a_hard_coded_one()
    {
        var run = (await W.RunAsync(Prev.Month, Prev.Year))!;
        await using var db = W.Db();
        int sid = (await db.Set<PayrollSlip>().FirstAsync(s => s.PayrollRunId == run.Id)).Id;
        string html = await (await h.C.GetAsync($"/compensation/slip/{sid}")).Content.ReadAsStringAsync();
        Assert.Contains(W.CompanyName, html);
        Assert.DoesNotContain("Solarkon", html);
        var pdf = await h.C.GetAsync($"/compensation/slip/{sid}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF"u8.ToArray(), (await pdf.Content.ReadAsByteArrayAsync())[..4]);
    }

    [ReferenceFact]
    public async Task S08_loan_instalment_reduces_the_loan_account()
    {
        int lid;
        string person;
        await using (var db = W.Db())
        {
            person = (await db.Users.FirstAsync(u => u.Id == W.Admin)).FullName;
            var loan = new LoanAdvanceRequest
            {
                UserId = W.Admin, RequestType = "loan", Amount = 12000, Purpose = "bike", InstallmentMonths = 12, MonthlyInstallment = 1000,
                RemainingAmount = 12000, Status = "approved", DisbursedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedAt = DateTime.UtcNow,
            };
            db.Add(loan);
            await db.SaveChangesAsync();
            lid = loan.Id;
        }
        var r = await h.PostAsync("/compensation/run-payroll", ("month", Third.Month.ToString()), ("year", Third.Year.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var run = (await W.RunAsync(Third.Month, Third.Year))!;
        var cr = await CreditsAsync(run.Id);
        Assert.Equal(1000m, cr[$"Loan #{lid} - {person}"]);
        Assert.False(cr.ContainsKey("Loan Deductions Clearing"));
        Assert.Equal(0m, cr.Values.Sum());
        await using (var db = W.Db())
            Assert.Equal(11000m, (await db.Set<LoanAdvanceRequest>().FirstAsync(l => l.Id == lid)).RemainingAmount);
        await W.AssertBooksTieAsync("payroll with a loan instalment");
    }

    [ReferenceFact]
    public async Task S10_new_profile_does_not_pay_basic_twice()
    {
        int nid = W.Employees[0];
        var r = await h.PostAsync("/compensation/create-profile", ("user_id", nid.ToString()), ("basic_salary", "40000"), ("effective_from", "2020-01-01"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal($"/compensation/edit-profile/{nid}", r.Headers.Location!.OriginalString);
        await using (var db = W.Db())
        {
            var names = await db.Set<PayrollComponent>().Where(c => c.Profile.UserId == nid).Select(c => c.Name).ToListAsync();
            Assert.DoesNotContain("Basic Salary", names);
            Assert.Equal(["House Rent", "Medical Allowance", "Conveyance"], names);
        }
        var mine = await h.PreviewAsync(Prev.Month, Prev.Year, user: nid);
        Assert.Equal(40000 * 1.45, mine.D("gross"));
        // A second profile for the same person is refused.
        r = await h.PostAsync("/compensation/create-profile", ("user_id", nid.ToString()), ("basic_salary", "40000"), ("effective_from", "2020-01-01"));
        Assert.Contains("Profile already exists", await h.FlashesAsync());
    }

    [ReferenceFact]
    public async Task S12_preview_json_with_an_active_loan()
    {
        var me = await h.PreviewAsync(Prev.Month, Prev.Year);
        var loans = me.GetProperty("active_loans");
        Assert.True(loans.GetArrayLength() > 0);
        Assert.Equal(11000, loans[0].GetProperty("remaining_amount").GetDouble());
    }

    [ReferenceFact]
    public async Task S13_an_existing_month_is_refused_unless_replaced()
    {
        var r = await h.PostAsync("/compensation/run-payroll", ("month", Prev.Month.ToString()), ("year", Prev.Year.ToString()));
        Assert.Equal("/compensation/run-payroll", r.Headers.Location!.OriginalString);
        Assert.Contains("Use 'Replace All' to override", await h.FlashesAsync());
        var old = (await W.RunAsync(Prev.Month, Prev.Year))!;
        r = await h.PostAsync("/compensation/run-payroll", ("month", Prev.Month.ToString()), ("year", Prev.Year.ToString()), ("replace_all", "1"));
        var now = (await W.RunAsync(Prev.Month, Prev.Year))!;
        Assert.NotEqual(old.Id, now.Id);
        Assert.Equal($"/compensation/run/{now.Id}", r.Headers.Location!.OriginalString);
        Assert.Empty(await W.VoucherLinesAsync("PRL", old.Id));
        string flashes = await h.FlashesAsync();
        Assert.Contains("Replaced existing payroll", flashes);
        Assert.Contains("Payroll completed for 2 employees", flashes);
        await W.AssertBooksTieAsync("payroll replaced");
    }

    [ReferenceFact]
    public async Task S14_employees_cannot_open_admin_pages_but_see_their_own_slips()
    {
        var emp = h.Host.ClientFor(W.Employees[0], W.Company);
        var r = await emp.GetAsync("/compensation/run-payroll");
        Assert.Equal("/dashboard", r.Headers.Location!.OriginalString);
        var mine = await emp.GetAsync("/compensation/");
        string html = await mine.Content.ReadAsStringAsync();
        Assert.Contains("My Compensation", html);
        await using var db = W.Db();
        int other = (await db.Set<PayrollSlip>().FirstAsync(s => s.UserId == W.Admin)).Id;
        Assert.Equal("/dashboard", (await emp.GetAsync($"/compensation/slip/{other}")).Headers.Location!.OriginalString);
        var json = await emp.PostFormWithTokenAsync("/compensation/payroll-preview-json", [], await emp.TokenFromAsync("/compensation/"));
        Assert.Equal(HttpStatusCode.Forbidden, json.StatusCode);
    }
}
