using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Payroll.Tests;

/// <summary>A company of 200 staff (the Python timings in docs/modules/payroll.md were taken on an identical twin).</summary>
public sealed class StaffHarness : IAsyncLifetime
{
    public const int Staff = 200;
    public World Net { get; private set; } = null!;
    public ModuleHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!Reference.Available) return;
        var people = Enumerable.Range(1, Staff - 1).Select(i => new World.Person($"Staff {i:000}", i % 17 == 0 ? "2025-09-10" : "2012-02-01")).ToList();
        Net = await World.CreateAsync("Speed Net", people);
        await SeedAsync(Net);
        Host = await ModuleHost.StartAsync(typeof(PayrollModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    /// <summary>Every kind of data the engine reads, spread over the staff.</summary>
    private static async Task SeedAsync(World w)
    {
        var all = new[] { w.Admin }.Concat(w.Employees).ToList();
        await using var db = w.Db();
        var now = DateTime.UtcNow;
        for (int i = 0; i < all.Count; i++)
        {
            var p = new PayrollProfile { UserId = all[i], BasicSalary = 40_000 + i * 731.25m, EffectiveFrom = new(2012, 2, 1), PaymentMethod = "bank_transfer",
                                         CreatedAt = now, UpdatedAt = now };
            p.PayrollComponents.Add(new PayrollComponent { Name = "House Rent", Type = "allowance", CalculationMethod = "percent_of_basic", Value = 40, IsTaxable = true });
            p.PayrollComponents.Add(new PayrollComponent { Name = "Medical", Type = "allowance", CalculationMethod = "percent_of_basic", Value = 10, IsTaxable = false });
            p.PayrollComponents.Add(new PayrollComponent { Name = "Welfare", Type = "deduction", CalculationMethod = "fixed", Value = 250, IsTaxable = false });
            db.Add(p);
        }
        var lwp = new LeaveType { Name = "Leave without pay", Code = "LWP", IsPaid = false, CarryForward = false, RequiresApproval = true };
        db.Add(lwp);
        await db.SaveChangesAsync();
        for (int i = 0; i < all.Count; i++)
        {
            int u = all[i];
            if (i % 10 == 1)
                db.Add(new SalaryRevision { UserId = u, PreviousBasic = 40_000 + i * 731.25m, NewBasic = 45_000 + i * 731.25m, Reason = "raise",
                                            ApprovedBy = w.Admin, EffectiveFrom = new(2025, 9, 16), CreatedAt = now });
            if (i % 7 == 2)
                db.Add(new LeaveRequest { UserId = u, LeaveTypeId = lwp.Id, StartDate = new(2025, 9, 3), EndDate = new(2025, 9, 4), TotalDays = 2,
                                          Reason = "x", Status = "approved", IsHalfDay = false, CreatedAt = now });
            if (i % 5 == 3)
                db.Add(new OvertimeAccount { UserId = u, Date = new(2025, 9, 8), RegularHours = 8, OvertimeHours = 4.5, CreatedAt = now });
            if (i % 9 == 4)
                db.Add(new LoanAdvanceRequest { UserId = u, RequestType = "loan", Amount = 60_000, InstallmentMonths = 12, MonthlyInstallment = 5_000,
                                                RemainingAmount = 60_000, Purpose = "x", Status = "approved",
                                                DisbursedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc), CreatedAt = now });
            if (i % 13 == 5)
                db.Add(new PfLoanRequest { UserId = u, Amount = 6_000, InstallmentMonths = 6, MonthlyInstallment = 1_000, RemainingAmount = 6_000,
                                           Purpose = "x", Status = "approved", CreatedAt = now });
        }
        db.Add(new PfConfig { EmployeeContributionPct = 5, EmployerContributionPct = 5, ContributionBase = "basic", MaxLoanPercentage = 50,
                              InterestRate = 0, MinServiceMonthsForLoan = 0, IsActive = true, UpdatedAt = now });
        foreach (var (lo, hi, rate, fixedAmount) in Accountix.Payroll.Services.PayrollMath.SlabsTy2026)
            db.Add(new IncomeTaxSlab { MinIncome = (decimal)lo, MaxIncome = (decimal)hi, RatePct = (decimal)rate, FixedAmount = (decimal)fixedAmount,
                                       IsActive = true, CreatedAt = now });
        db.Add(new PayrollSetting { OvertimeMultiplier = 1.5m, OvertimeHoursBasis = 160, EobiEnabled = true, EobiWageBase = 37000, EobiEmployeePct = 1,
                                    EobiEmployerPct = 5, SurchargeThreshold = 10_000_000, SurchargePct = 9, GratuityEnabled = false,
                                    GratuityBasis = "basic", GratuityDaysPerYear = 30, LeaveEncashmentEnabled = false });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Payroll for 200 employees is set-based: the preview and the run read each table
/// once (profiles, components, revisions, memberships, leave, overtime, loans, PF
/// loans, year-to-date slips, settings, slabs, PF config), never once per employee.
/// Budgets (docs/SPEED_AND_CONCURRENCY.md): pages ≤ 800 ms and ≤ 40 queries, the run
/// ≤ 2 s, files ≤ 3 s. (Python's times for the same pages, taken on an identical company
/// before the Python app was removed, are kept in docs/modules/payroll.md as history.)
/// </summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed class SpeedTests(StaffHarness h, ITestOutputHelper output) : IClassFixture<StaffHarness>
{
    private World W => h.Net;
    private PageClient? _c;
    private PageClient C => _c ??= h.Host.ClientFor(W.Admin, W.Company);

    private void Record(string what, double ms, int queries)
    {
        output.WriteLine($"{ms:F0} ms, {queries} queries, {what}");
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_TIMINGS") is { Length: > 0 } file)
            File.AppendAllText(file, $"{ms:F0} ms, {queries} queries, {what} (payroll, {StaffHarness.Staff} staff)\n");
    }

    private async Task<(HttpResponseMessage R, double Ms, int Queries)> TimedPostAsync(string url, params (string Key, string Value)[] fields)
    {
        string token = await C.TokenFromAsync("/compensation/");
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList();
        h.Host.Queries.Reset();
        var sw = Stopwatch.StartNew();
        var r = await C.PostFormWithTokenAsync(url, form, token);
        await r.Content.ReadAsByteArrayAsync();
        sw.Stop();
        int q = h.Host.Queries.Count;
        Record($"POST {url}", sw.Elapsed.TotalMilliseconds, q);
        return (r, sw.Elapsed.TotalMilliseconds, q);
    }

    private static int MostRepeated(IReadOnlyList<string> top) =>
        top.Count == 0 ? 0 : int.Parse(top[0][..top[0].IndexOf('×')], System.Globalization.CultureInfo.InvariantCulture);

    private async Task<PageTiming> PageAsync(string url, int ms, int queries)
    {
        var t = await h.Host.MeasureAsync(C, url);
        Record($"GET {url}", t.Elapsed.TotalMilliseconds, t.Queries);
        return t.Within(ms, queries);
    }

    [ReferenceFact]
    public async Task S01_the_run_page_previews_200_staff_in_a_fixed_number_of_queries()
    {
        var t = await PageAsync("/compensation/run-payroll", 800, 40);
        Assert.Contains("Staff 199", t.Body);
    }

    [ReferenceFact]
    public async Task S02_the_preview_json_and_the_run_are_set_based()
    {
        // A first month (it also warms the run path), then the timed one: September reads August's slips.
        var (aug, _, _) = await TimedPostAsync("/compensation/run-payroll", ("month", "8"), ("year", "2025"));
        Assert.Equal(HttpStatusCode.Redirect, aug.StatusCode);
        // A second warm-up (the first run of a process compiles every query and creates the accounts).
        await TimedPostAsync("/compensation/run-payroll", ("month", "8"), ("year", "2025"), ("replace_all", "1"));
        var (pv, pvMs, pvQ) = await TimedPostAsync("/compensation/payroll-preview-json", ("month", "9"), ("year", "2025"));
        Assert.Equal(HttpStatusCode.OK, pv.StatusCode);
        Assert.Equal(StaffHarness.Staff, JsonDocument.Parse(await pv.Content.ReadAsStringAsync()).RootElement.GetProperty("employees").GetArrayLength());
        Assert.True(pvQ <= 20, $"preview: {pvQ} queries\n{string.Join("\n", h.Host.Queries.Repeated())}");
        Assert.True(pvMs <= 800, $"preview: {pvMs:F0} ms");
        var (run, ms, q) = await TimedPostAsync("/compensation/run-payroll", ("month", "9"), ("year", "2025"));
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);
        var top = h.Host.Queries.Repeated(3);
        // Nothing runs once per employee (200 staff): the busiest statement repeats a handful of
        // times (the accounts the run fetches or creates, period checks), whatever the staff.
        Assert.True(q <= 100 && MostRepeated(top) <= 40, $"run: {q} queries\n{string.Join("\n", top)}");
        Assert.True(ms <= 2000, $"run: {ms:F0} ms");
        await using var db = W.Db();
        int rid = (await W.RunAsync(9, 2025))!.Id;
        Assert.Equal(StaffHarness.Staff, await db.Set<PayrollSlip>().CountAsync(s => s.PayrollRunId == rid));
    }

    [ReferenceFact]
    public async Task S03_run_detail_slips_bank_sheet_and_lists()
    {
        int rid = (await W.RunAsync(9, 2025))!.Id;
        await PageAsync($"/compensation/run/{rid}", 800, 40);
        await PageAsync($"/compensation/export-bank-ledger/{rid}", 3000, 40);
        await PageAsync("/compensation/", 800, 40);
        await PageAsync("/compensation/download-template", 3000, 40);
        await PageAsync("/compensation/loans", 800, 40);
        await PageAsync("/compensation/revisions", 800, 40);
        await PageAsync("/pf/", 800, 40);
        await using var db = W.Db();
        int sid = await db.Set<PayrollSlip>().Where(s => s.PayrollRunId == rid).MinAsync(s => s.Id);
        await PageAsync($"/compensation/slip/{sid}", 800, 40);
        await PageAsync($"/compensation/slip/{sid}/pdf", 3000, 40);
    }

    [ReferenceFact]
    public async Task S04_replacing_and_deleting_200_slips_stay_within_budget()
    {
        var (rep, ms, q) = await TimedPostAsync("/compensation/run-payroll", ("month", "9"), ("year", "2025"), ("replace_all", "1"));
        Assert.Equal(HttpStatusCode.Redirect, rep.StatusCode);
        Assert.True(ms <= 2500 && q <= 120 && MostRepeated(h.Host.Queries.Repeated(1)) <= 40, $"replace: {ms:F0} ms, {q} queries\n{string.Join("\n", h.Host.Queries.Repeated(3))}");
        int rid = (await W.RunAsync(9, 2025))!.Id;
        var (del, dms, dq) = await TimedPostAsync($"/compensation/run/{rid}/delete");
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.True(dms <= 2000 && dq <= 40, $"delete: {dms:F0} ms, {dq} queries\n{string.Join("\n", h.Host.Queries.Repeated(3))}");
        await W.AssertBooksTieAsync("200-staff run deleted");
    }
}
