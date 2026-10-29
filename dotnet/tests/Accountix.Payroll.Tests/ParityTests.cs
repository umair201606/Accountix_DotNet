using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

/// <summary>
/// The parity company: staff, packages, leave, overtime, loans, PF, policy and adjustments
/// chosen to reach every rule (proration, joiner, leaver with gratuity and leave encashment,
/// raise mid-month, unpaid leave across months, overtime, a loan instalment override, a PF
/// loan, a bonus with a comma, a tax override, a custom deduction, a salary_advance title).
/// Built by .NET; the Python app ran the same company and steps once, and its answer is the
/// golden fixture (Golden/payroll_parity.json, from Golden/capture/payroll_parity.py).
/// </summary>
public sealed class ParityHarness : IAsyncLifetime
{
    public World Net { get; private set; } = null!;
    public ModuleHost Host { get; private set; } = null!;

    public static readonly World.Person[] Staff =
    [
        new("Bina Joiner", "2025-08-12"), new("Kamal Leaver", "2010-03-17"),
        new("Laila Borrower", "2018-01-01"), new("Raza Raised", "2016-05-05"),
    ];

    public async Task InitializeAsync()
    {
        if (!Reference.Available) return;
        Net = await World.CreateAsync("Parity Net", Staff);
        await SeedAsync(Net);
        Host = await ModuleHost.StartAsync(typeof(PayrollModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    /// <summary>The seed (the capture script inserts the same rows, column for column).</summary>
    private static async Task SeedAsync(World w)
    {
        int admin = w.Admin, bina = w.Employees[0], kamal = w.Employees[1], laila = w.Employees[2], raza = w.Employees[3];
        await w.ProfileAsync(admin, 150_000, "2000-01-01", ("House Rent", "allowance", "percent_of_basic", 40m, true),
                             ("Medical", "allowance", "percent_of_basic", 10m, false), ("Welfare fund", "deduction", "fixed", 500m, false));
        await w.ProfileAsync(bina, 87_333.33m, "2025-08-12", ("Conveyance", "allowance", "fixed", 4321.5m, true));
        await w.ProfileAsync(kamal, 260_000, "2010-03-17", ("House Rent", "allowance", "percent_of_basic", 45m, true));
        await w.ProfileAsync(laila, 45_000, "2018-01-01");
        await w.ProfileAsync(raza, 135_000, "2016-05-05", ("Utilities", "allowance", "percent_of_basic", 7.5m, true));
        int lwp = await w.LeaveTypeAsync("Leave without pay", "LWP", paid: false, carryForward: false);
        int al = await w.LeaveTypeAsync("Annual leave", "AL", paid: true, carryForward: true);
        await using var db = w.Db();
        var now = DateTime.UtcNow;
        db.Add(new SalaryRevision { UserId = raza, PreviousBasic = 120_000, NewBasic = 135_000, Reason = "Raise", ApprovedBy = admin,
                                    EffectiveFrom = new(2025, 8, 16), CreatedAt = now });
        db.Add(new LeaveRequest { UserId = bina, LeaveTypeId = lwp, StartDate = new(2025, 8, 20), EndDate = new(2025, 8, 21), TotalDays = 2,
                                  Reason = "family", Status = "approved", IsHalfDay = false, CreatedAt = now });
        db.Add(new LeaveRequest { UserId = raza, LeaveTypeId = lwp, StartDate = new(2025, 7, 30), EndDate = new(2025, 8, 2), TotalDays = 3.5m,
                                  Reason = "travel", Status = "approved", IsHalfDay = false, CreatedAt = now });
        db.Add(new LeaveQuota { UserId = kamal, LeaveTypeId = al, Year = 2025, Total = 14, Used = 6.5m, Pending = 0, Remaining = 7.5m });
        db.Add(new OvertimeAccount { UserId = bina, Date = new(2025, 8, 25), RegularHours = 8, OvertimeHours = 7.5, CreatedAt = now });
        db.Add(new OvertimeAccount { UserId = admin, Date = new(2025, 7, 10), RegularHours = 8, OvertimeHours = 3, CreatedAt = now });
        db.Add(new LoanAdvanceRequest { UserId = laila, RequestType = "advance", Amount = 100_000, InstallmentMonths = 2, MonthlyInstallment = 50_000,
                                        RemainingAmount = 100_000, Purpose = "wedding", Status = "approved",
                                        DisbursedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), CreatedAt = now });
        db.Add(new PfLoanRequest { UserId = laila, Amount = 9_000, InstallmentMonths = 3, MonthlyInstallment = 3_000, RemainingAmount = 9_000,
                                   Purpose = "fees", Status = "approved", CreatedAt = now });
        db.Add(new PfConfig { EmployeeContributionPct = 5, EmployerContributionPct = 5, ContributionBase = "basic", MaxLoanPercentage = 50,
                              InterestRate = 0, MinServiceMonthsForLoan = 0, IsActive = true, UpdatedAt = now });
        await db.SaveChangesAsync();
        await db.Memberships.Where(m => m.CompanyId == w.Company && m.UserId == kamal)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateOfLeaving, new DateOnly(2025, 8, 22)));
    }

    public static readonly (string, string)[] Policy =
    [
        ("action", "policy"), ("overtime_multiplier", "1.5"), ("overtime_hours_basis", "160"), ("eobi_enabled", "1"),
        ("eobi_wage_base", "37000"), ("eobi_employee_pct", "1"), ("eobi_employer_pct", "5"), ("surcharge_threshold", "10000000"),
        ("surcharge_pct", "9"), ("gratuity_enabled", "1"), ("gratuity_basis", "gross"), ("gratuity_days_per_year", "30"),
        ("leave_encashment_enabled", "1"),
    ];

    /// <summary>The August adjustments: a bonus with a comma, a custom deduction, a tax override, a loan instalment override.</summary>
    public static async Task<string> AugustAdjustmentsAsync(World w)
    {
        await using var db = w.Db();
        int loan = await db.Set<LoanAdvanceRequest>().Where(l => l.UserId == w.Employees[2]).Select(l => l.Id).SingleAsync();
        return JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [w.Employees[0].ToString()] = new() { ["bonus"] = "12,345.67" },
            [w.Admin.ToString()] = new() { ["custom_deduction"] = "333.33", ["allowances_override"] = "" },
            [w.Employees[3].ToString()] = new() { ["income_tax"] = "7777.777" },
            [w.Employees[2].ToString()] = new() { [$"loan_{loan}"] = "60000" },
        });
    }
}

/// <summary>
/// Golden: everything the steps write must be what the Python app wrote for the same company
/// and steps, to the paisa and to the character: settings, preview figures, slips
/// (components_json text included), run totals, journal lines, loan recoveries, PF rows and
/// contributions, notifications, salary payment, Replace All, PF withdrawal / profit /
/// settlement / manual entry, loan pay-out and delete. Both sides' rows are read with the same
/// SQL (Golden/payroll_parity_views.json) and normalised alike: people as positions (0 the
/// admin), the admin's tagged name, loan ids, generated PF/LOAN numbers, today's date.
/// </summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed class ParityTests(ParityHarness h) : IClassFixture<ParityHarness>
{
    private static readonly JsonElement Fixture = Golden.Load("payroll_parity.json");
    private static readonly JsonElement Views = Golden.Load("payroll_parity_views.json");
    private PageClient? _c;
    private string? _token;

    private static JsonElement Step(string step) => Fixture.GetProperty("steps").GetProperty(step);

    private async Task<int> NetPostAsync(string url, params (string Key, string Value)[] fields)
    {
        _c ??= h.Host.ClientFor(h.Net.Admin, h.Net.Company);
        _token ??= await _c.TokenFromAsync("/compensation/");
        return (int)(await _c.PostFormWithTokenAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)), _token)).StatusCode;
    }

    private static void SameStatus(string step, IReadOnlyList<int> net) =>
        Assert.Equal(Step(step).GetProperty("status").EnumerateArray().Select(e => e.GetInt32()), net);

    // ── normalising both sides alike ────────────────────────────────────

    /// <summary>What differs between two companies built from the same steps: who is who, generated ids, the day.</summary>
    private sealed record Ids(IReadOnlyList<int> People, string AdminName, IReadOnlyList<int> Loans, IReadOnlyList<int> PfLoans, string Today);

    private static Ids PythonIds()
    {
        var w = Fixture.GetProperty("world");
        static List<int> L(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToList();
        return new Ids(L(w.GetProperty("users")), w.GetProperty("admin_name").GetString()!, L(w.GetProperty("loans")), L(w.GetProperty("pf_loans")),
                       Fixture.GetProperty("today").GetString()!);
    }

    private async Task<Ids> NetIdsAsync()
    {
        await using var db = h.Net.Db();
        var loans = await db.Set<LoanAdvanceRequest>().OrderBy(l => l.Id).Select(l => l.Id).ToListAsync();
        var pfLoans = await db.Set<PfLoanRequest>().OrderBy(l => l.Id).Select(l => l.Id).ToListAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")));
        return new Ids(h.Net.People, "Admin " + h.Net.Tag, loans, pfLoans, today.ToString("yyyy-MM-dd"));
    }

    private static string Norm(string? s, Ids ids)
    {
        if (s is null) return "null";
        s = s.Replace(ids.AdminName, "ADMIN");
        foreach (int l in ids.Loans) s = Regex.Replace(s, $@"#{l}\b", "#LOAN");
        foreach (int l in ids.PfLoans) s = Regex.Replace(s, $@"#{l}\b", "#PFLOAN");
        s = Regex.Replace(s, @"(PF-(WD|LOAN|SETTLE|ADJ)-|PF-PROFIT-\d{6}-|LOAN-DSB-)\d+", "$1N");
        return s.Replace(ids.Today, "TODAY");
    }

    private static string Row(IReadOnlyList<string?> cells, int? person, Ids ids) =>
        string.Join(" | ", cells.Select((c, i) => i == person && c is not null ? $"P{ids.People.ToList().IndexOf(int.Parse(c))}" : Norm(c, ids)));

    private static int? PersonColumn(string view) =>
        Views.GetProperty(view).TryGetProperty("person", out var p) ? p.GetInt32() : null;

    private static string Sql(string view, params (string Key, object Value)[] args)
    {
        string sql = Views.GetProperty(view).GetProperty("sql").GetString()!;
        foreach (var (k, v) in args) sql = sql.Replace("{" + k + "}", Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
        return sql;
    }

    /// <summary>Python's rows for a view, as the fixture holds them, normalised.</summary>
    private static List<string> PythonRows(JsonElement rows, string view)
    {
        var ids = PythonIds();
        return rows.EnumerateArray().Select(r => Row(r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString()).ToList(),
                                                     PersonColumn(view), ids)).ToList();
    }

    /// <summary>.NET's rows for a view: the same SQL on .NET's company, normalised.</summary>
    private async Task<List<string>> NetRowsAsync(string view, params (string Key, object Value)[] args)
    {
        var ids = await NetIdsAsync();
        await using var db = h.Net.Db();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql(view, [("cid", h.Net.Company), .. args]);
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(Row(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetString(i)).ToList(), PersonColumn(view), ids));
        return rows;
    }

    /// <summary>One view of one step: .NET's rows equal Python's.</summary>
    private async Task SameAsync(string step, string key, string view, params (string Key, object Value)[] args)
    {
        var expected = Step(step);
        foreach (string part in key.Split('.')) expected = expected.GetProperty(part);
        Golden.Same(PythonRows(expected, view), await NetRowsAsync(view, args), $"{step} {key}");
    }

    private async Task SameSideRowsAsync(string step)
    {
        foreach (string v in new[] { "pf_ledger", "pf_contributions", "loan_repayments", "loans", "pf_loans", "notifications" })
            await SameAsync(step, $"side.{v}", v);
    }

    // ── the steps ───────────────────────────────────────────────────────

    [ReferenceFact]
    public async Task S01_settings_saved_are_pythons()
    {
        SameStatus("S01", [await NetPostAsync("/compensation/tax-settings", ("action", "seed_defaults")),
                           await NetPostAsync("/compensation/tax-settings", ParityHarness.Policy)]);
        var py = Step("S01").GetProperty("settings").EnumerateArray().ToList();
        var ids = PythonIds();
        Golden.Same(py.Select(r => Row(r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString()).ToList(), null, ids)).ToList(),
                    [.. await NetRowsAsync("slabs"), .. await NetRowsAsync("payroll_settings")], "S01 slabs and payroll settings");
    }

    [ReferenceFact]
    public async Task S02_july_payroll_is_pythons()
    {
        SameStatus("S02", [await NetPostAsync("/compensation/run-payroll", ("month", "7"), ("year", "2025"))]);
        await SameAsync("S02", "slips", "slips", ("month", 7), ("year", 2025));
        await SameAsync("S02", "run", "run", ("month", 7), ("year", 2025));
        await SameAsync("S02", "journal", "journal", ("types", "'PRL'"));
    }

    [ReferenceFact]
    public async Task S03_august_preview_figures_are_pythons()
    {
        _c ??= h.Host.ClientFor(h.Net.Admin, h.Net.Company);
        _token ??= await _c.TokenFromAsync("/compensation/");
        var r = await _c.PostFormWithTokenAsync("/compensation/payroll-preview-json",
            [new("month", "8"), new("year", "2025"), new("adjustments", await ParityHarness.AugustAdjustmentsAsync(h.Net))], _token);
        SameStatus("S03", [(int)r.StatusCode]);
        Golden.Same(PreviewShape(Step("S03").GetProperty("preview").GetString()!, PythonIds().People),
                    PreviewShape(await r.Content.ReadAsStringAsync(), h.Net.People), "S03 August preview");
    }

    /// <summary>Every figure of every employee's preview, keys sorted, user ids as positions.</summary>
    private static List<string> PreviewShape(string json, IReadOnlyList<int> people)
    {
        var order = people.ToList();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string[] skip = ["user_id", "name", "loans", "pf_loans", "active_loans", "components"];
        using var doc = JsonDocument.Parse(json);
        var rows = new List<string>();
        foreach (var e in doc.RootElement.GetProperty("employees").EnumerateArray().OrderBy(e => order.IndexOf(e.GetProperty("user_id").GetInt32())))
        {
            string who = order.IndexOf(e.GetProperty("user_id").GetInt32()).ToString(inv);
            foreach (var p in e.EnumerateObject().Where(p => !skip.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
                rows.Add($"{who} {p.Name} = {Flat(p.Value)}");
            rows.Add($"{who} loans = {string.Join(",", e.GetProperty("loans").EnumerateArray().Select(l => l.GetProperty("amount").GetDouble().ToString("R", inv)))}");
            rows.Add($"{who} pf_loans = {string.Join(",", e.GetProperty("pf_loans").EnumerateArray().Select(l => l.GetProperty("amount").GetDouble().ToString("R", inv)))}");
            rows.Add($"{who} active_loans = {string.Join(",", e.GetProperty("active_loans").EnumerateArray().Select(l => $"{l.GetProperty("monthly_installment").GetDouble().ToString("R", inv)}/{l.GetProperty("remaining_amount").GetDouble().ToString("R", inv)}"))}");
            rows.Add($"{who} components = {Flat(e.GetProperty("components"))}");
        }
        return rows;

        string Flat(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble().ToString("R", inv),
            JsonValueKind.Object => "{" + string.Join(", ", v.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}: {Flat(p.Value)}")) + "}",
            _ => v.ToString(),
        };
    }

    [ReferenceFact]
    public async Task S04_august_payroll_with_proration_leaver_settlement_loans_and_overrides_is_pythons()
    {
        SameStatus("S04", [await NetPostAsync("/compensation/run-payroll", ("month", "8"), ("year", "2025"),
                                              ("adjustments", await ParityHarness.AugustAdjustmentsAsync(h.Net)))]);
        await SameAsync("S04", "slips", "slips", ("month", 8), ("year", 2025));
        await SameAsync("S04", "run", "run", ("month", 8), ("year", 2025));
        await SameAsync("S04", "journal", "journal", ("types", "'PRL'"));
        await SameSideRowsAsync("S04");
        Assert.Empty(Fixture.GetProperty("python_books_failures").EnumerateArray());
        await h.Net.AssertBooksTieAsync("August");
    }

    [ReferenceFact]
    public async Task S05_salary_payment_and_replace_are_pythons()
    {
        int run8 = (await h.Net.RunAsync(8, 2025))!.Id;
        var status = new List<int> { await NetPostAsync($"/compensation/run/{run8}/pay", ("account_id", h.Net.Cash.ToString()), ("paid_on", "2025-09-01")) };
        await SameAsync("S05", "journal_pay", "journal", ("types", "'PRL-PAY'"));
        await SameAsync("S05", "run", "run", ("month", 8), ("year", 2025));
        // July replaced: the same journals and rows come back.
        status.Add(await NetPostAsync("/compensation/run-payroll", ("month", "7"), ("year", "2025"), ("replace_all", "1")));
        SameStatus("S05", status);
        await SameAsync("S05", "slips", "slips", ("month", 7), ("year", 2025));
        await SameAsync("S05", "journal", "journal", ("types", "'PRL'"));
        await SameSideRowsAsync("S05");
    }

    [ReferenceFact]
    public async Task S06_pf_movements_are_pythons()
    {
        // A member asks; the admin pays; profit is shared; the admin is settled; a manual entry.
        var laila = h.Host.ClientFor(h.Net.Employees[2], h.Net.Company);
        var status = new List<int>
        {
            (int)(await laila.PostFormWithTokenAsync("/pf/request-withdrawal", [new("amount", "1234.5"), new("reason", "fees")],
                                                     await laila.TokenFromAsync("/pf/"))).StatusCode,
        };
        int? wid;
        await using (var db = h.Net.Db()) wid = await db.Set<PfWithdrawalRequest>().MaxAsync(w => (int?)w.Id);
        if (wid is null)
        {
            string page = await (await laila.GetAsync("/pf/")).Content.ReadAsStringAsync();
            Assert.Fail($"no withdrawal request filed (status {status[0]}): " +
                        string.Join(" | ", Regex.Matches(page, "<span class=\"app-flash-msg\">(.*?)</span>", RegexOptions.Singleline).Select(m => m.Groups[1].Value)));
        }
        string cash = h.Net.Cash.ToString();
        status.Add(await NetPostAsync($"/pf/approve-withdrawal/{wid}", ("account_id", cash), ("paid_on", "2025-09-05")));
        status.Add(await NetPostAsync("/pf/distribute-profit", ("month", "9"), ("year", "2025"), ("total_profit", "1234.56")));
        status.Add(await NetPostAsync($"/pf/settle/{h.Net.Admin}", ("account_id", cash), ("paid_on", "2025-09-30")));
        status.Add(await NetPostAsync("/pf/add-ledger-entry", ("user_id", h.Net.Employees[3].ToString()), ("type", "debit"), ("amount", "77.7"),
                                      ("description", "Correction"), ("counter_account_id", cash)));
        SameStatus("S06", status);
        await SameAsync("S06", "journal", "journal", ("types", "'PF-WD','PF-PROFIT','PF-SETTLE','PF-ADJ'"));
        await SameSideRowsAsync("S06");
        await SameAsync("S06", "settlement", "pf_settlement");
    }

    [ReferenceFact]
    public async Task S07_loan_pay_out_and_delete_run_are_pythons()
    {
        int loan;
        await using (var db = h.Net.Db())
        {
            var l = new LoanAdvanceRequest { UserId = h.Net.Employees[3], RequestType = "salary_advance", Amount = 30_000.5m, InstallmentMonths = 4,
                                             RemainingAmount = 30_000.5m, Purpose = "car repair", Status = "approved", CreatedAt = DateTime.UtcNow };
            db.Add(l);
            await db.SaveChangesAsync();
            loan = l.Id;
        }
        var status = new List<int> { await NetPostAsync($"/compensation/loans/{loan}/disburse", ("account_id", h.Net.Cash.ToString()), ("paid_on", "2025-09-10")) };
        await SameAsync("S07", "journal_dsb", "journal", ("types", "'LOAN-DSB'"));
        int july = (await h.Net.RunAsync(7, 2025))!.Id;
        status.Add(await NetPostAsync($"/compensation/run/{july}/delete"));
        SameStatus("S07", status);
        await SameAsync("S07", "journal", "journal", ("types", "'PRL'"));
        await SameSideRowsAsync("S07");
        await h.Net.AssertBooksTieAsync("delete");
    }
}
