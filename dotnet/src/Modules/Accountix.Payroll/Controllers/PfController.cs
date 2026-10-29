using System.Text.RegularExpressions;

namespace Accountix.Payroll.Controllers;

/// <summary>
/// Provident fund (hr_app/routes/pf.py): contributions, withdrawals, PF loans,
/// profit distribution, final settlement and manual ledger entries. Every
/// movement of money out of the fund posts to PF Payable; fund movements are
/// queued one at a time per company ("pf"), as in Python.
/// </summary>
[Area("Payroll")]
[Module(Modules.Hr)]
[Route("pf")]
public sealed partial class PfController(AccountixDbContext db, PayrollPeople people, PayrollPosting posting, ILedger ledger,
                                         CompanyClock clock) : Controller
{
    private EffectiveAccess Me => HttpContext.Access()!;
    private int Cid => Me.CompanyId!.Value;

    /// <summary>A member's fund balance: every PF ledger credit less every debit (in SQL, as Python).</summary>
    private async Task<double> BalanceAsync(int userId, CancellationToken ct)
    {
        var sums = await db.Set<PfLedger>().Where(r => r.UserId == userId)
            .GroupBy(r => 1).Select(g => new { Cr = g.Sum(r => r.Credit) ?? 0m, Dr = g.Sum(r => r.Debit) ?? 0m })
            .FirstOrDefaultAsync(ct);
        return sums is null ? 0.0 : Py.F(sums.Cr - sums.Dr);
    }

    /// <summary>Every member's balance (credits less debits), in user-id order.</summary>
    private async Task<List<(int UserId, double Balance)>> BalancesAsync(CancellationToken ct) =>
        (await db.Set<PfLedger>().GroupBy(r => r.UserId)
            .Select(g => new { g.Key, Bal = (g.Sum(r => r.Credit) ?? 0m) - (g.Sum(r => r.Debit) ?? 0m) })
            .OrderBy(x => x.Key).ToListAsync(ct))
        .Select(x => (x.Key, Py.F(x.Bal))).ToList();

    /// <summary>request.is_json or Accept's best value is application/json (werkzeug MIMEAccept.best).</summary>
    private bool WantsJson()
    {
        if (Request.HasJsonContentType()) return true;
        string accept = Request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept)) return false;
        var items = accept.Split(',').Select((raw, index) =>
        {
            var parts = raw.Split(';').Select(p => p.Trim()).ToList();
            double q = 1;
            var rest = new List<string> { parts[0] };
            foreach (var p in parts.Skip(1))
                if (p.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                    q = Py.ParseFloat(p[2..]) ?? 1;
                else
                    rest.Add(p);
            string value = string.Join(";", rest);
            var spec = Regex.Split(value, @"/|(?:\s*;\s*)").Select(x => x != "*").ToArray();
            return (value, q, spec, index);
        }).Where(i => i.value.Length > 0).ToList();
        if (items.Count == 0) return false;
        var best = items.OrderByDescending(i => i.spec, SpecificityComparer.Instance).ThenByDescending(i => i.q).ThenBy(i => i.index).First();
        return string.Equals(best.value, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SpecificityComparer : IComparer<bool[]>
    {
        public static readonly SpecificityComparer Instance = new();

        public int Compare(bool[]? a, bool[]? b)
        {
            a ??= []; b ??= [];
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i] ? 1 : -1;
            return a.Length.CompareTo(b.Length);
        }
    }

    /// <summary>_reply: JSON for scripts; flash and back to the PF page for the plain forms.</summary>
    private IActionResult Reply(bool ok, string msg, int status = 200)
    {
        if (WantsJson())
            return ok
                ? new JsonResult(new Dictionary<string, object> { ["success"] = true, ["message"] = msg }) { StatusCode = status }
                : new JsonResult(new Dictionary<string, object> { ["error"] = msg }) { StatusCode = status };
        this.Flash(msg, ok ? "success" : "danger");
        return Redirect("/pf/");
    }

    /// <summary>_has_button_perm: admins always; otherwise the role held in THIS company.</summary>
    private async Task<bool> HasButtonPermAsync(string code, CancellationToken ct)
    {
        if (Me.IsAdmin) return true;
        int roleId = Me.Membership?.RoleId ?? Me.User.RoleId;
        return await db.Set<ButtonPermission>().AnyAsync(b => b.RoleId == roleId && b.ButtonCode == code && b.IsGranted == true, ct);
    }

    /// <summary>_paid_from: the cash/bank account and date the payment is made from, or the refusal.</summary>
    private async Task<(ChartOfAccount? Account, DateOnly When, IActionResult? Error)> PaidFromAsync(CancellationToken ct)
    {
        var account = await people.CashBankAccountAsync(Request.Form["account_id"].FirstOrDefault(), ct);
        if (account is null)
            return (null, default, Reply(false, "Choose the bank or cash account the payment is made from.", 400));
        return (account, DateOnly.FromDateTime(DocumentDates.Parse(Request.Form["paid_on"].FirstOrDefault(), null, clock)), null);
    }

    private string? Form(string key) => Request.HasFormContentType ? Request.Form[key].FirstOrDefault() : null;

    private void Heading() => ViewData["Heading"] = "Accountix ERP";

    // ── Pages ──

    [HttpGet("")]
    [AnyMember]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var config = await db.Set<PfConfig>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        Heading();
        if (Me.IsAdmin)
        {
            var contributions = await db.Set<PfContribution>().AsNoTracking()
                .OrderByDescending(c => c.Year).ThenByDescending(c => c.Month).ThenBy(c => c.Id).Take(50)
                .Select(c => new NamedRow<PfContribution>(c, c.User.FullName)).ToListAsync(ct);
            var withdrawals = await db.Set<PfWithdrawalRequest>().AsNoTracking()
                .OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id).Take(20)
                .Select(w => new NamedRow<PfWithdrawalRequest>(w, w.User.FullName)).ToListAsync(ct);
            var loans = await db.Set<PfLoanRequest>().AsNoTracking()
                .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Take(20)
                .Select(l => new NamedRow<PfLoanRequest>(l, l.User.FullName)).ToListAsync(ct);
            var balances = await BalancesAsync(ct);
            var members = await people.MembersAsync(balances.Select(b => b.UserId), ct);
            var listed = balances
                .Where(b => members.ContainsKey(b.UserId) || (Me.User.SuperAdmin && b.UserId == Me.User.Id))
                .Select(b => (b.UserId, FullName: members.TryGetValue(b.UserId, out var u) ? u.FullName : Me.User.FullName,
                              Balance: Py.Round(b.Balance, 2)))
                .Where(m => Math.Abs(m.Balance) > 0.004)
                .OrderBy(m => m.FullName ?? "", StringComparer.Ordinal).ToList();
            return View("Index", new PfAdminModel
            {
                Config = config, Contributions = contributions, Withdrawals = withdrawals, Loans = loans,
                Accounts = await ledger.CashBankAccountsAsync(ct), Today = clock.Today, Members = listed,
                CanCalc = await HasButtonPermAsync("BUTTON_CALCULATE_CONTRIBUTIONS", ct),
                CanProfit = await HasButtonPermAsync("BUTTON_CALCULATE_PROFIT", ct),
                CanSettle = await HasButtonPermAsync("BUTTON_PROCESS_SETTLEMENT", ct),
            });
        }
        int me = Me.User.Id;
        return View("Employee", new PfEmployeeModel
        {
            Config = config,
            Contributions = await db.Set<PfContribution>().AsNoTracking().Where(c => c.UserId == me)
                .OrderByDescending(c => c.Year).ThenByDescending(c => c.Month).ThenBy(c => c.Id).ToListAsync(ct),
            Ledger = await db.Set<PfLedger>().AsNoTracking().Where(r => r.UserId == me)
                .OrderByDescending(r => r.TransactionDate).ThenBy(r => r.Id).Take(30).ToListAsync(ct),
            Balance = await BalanceAsync(me, ct),
            Withdrawals = await db.Set<PfWithdrawalRequest>().AsNoTracking().Where(w => w.UserId == me)
                .OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id).ToListAsync(ct),
            Loans = await db.Set<PfLoanRequest>().AsNoTracking().Where(l => l.UserId == me)
                .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).ToListAsync(ct),
        });
    }

    [HttpGet("config")]
    [AnyMember]
    public async Task<IActionResult> Config(CancellationToken ct)
    {
        if (!Me.IsAdmin)
        {
            this.Flash("Access denied.", "danger");
            return Redirect("/dashboard");
        }
        // A new configuration is shown with the form's defaults and saved only on POST.
        var cfg = await db.Set<PfConfig>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct) ?? new PfConfig();
        Heading();
        return View("Config", new PfConfigModel { Config = cfg });
    }

    [HttpPost("config")]
    [AnyMember]
    public async Task<IActionResult> ConfigPost(CancellationToken ct)
    {
        if (!Me.IsAdmin)
        {
            this.Flash("Access denied.", "danger");
            return Redirect("/dashboard");
        }
        var cfg = await db.Set<PfConfig>().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        bool isNew = cfg is null;
        cfg ??= new PfConfig();
        var f = Request.Form;
        var before = (cfg.EmployeeContributionPct, cfg.EmployerContributionPct, cfg.ContributionBase, cfg.MaxLoanPercentage,
                      cfg.InterestRate, cfg.MinServiceMonthsForLoan);
        cfg.EmployeeContributionPct = Py.Dec(Py.ParseFloat(f["employee_pct"].FirstOrDefault()) ?? 5.0);
        cfg.EmployerContributionPct = Py.Dec(Py.ParseFloat(f["employer_pct"].FirstOrDefault()) ?? 5.0);
        if (f["contribution_base"].FirstOrDefault() is "basic" or "gross")
            cfg.ContributionBase = f["contribution_base"].FirstOrDefault();
        cfg.MaxLoanPercentage = Py.Dec(Py.ParseFloat(f["max_loan_pct"].FirstOrDefault()) ?? 50.0);
        cfg.InterestRate = Py.Dec(Py.ParseFloat(f["interest_rate"].FirstOrDefault()) ?? 0.0);
        cfg.MinServiceMonthsForLoan = Py.ParseInt(f["min_months"].FirstOrDefault()) ?? 12;
        if (isNew)
        {
            cfg.ContributionBase ??= "basic";
            cfg.IsActive = true;
            cfg.UpdatedAt = clock.UtcNow;
            db.Add(cfg);
        }
        else if (before != (cfg.EmployeeContributionPct, cfg.EmployerContributionPct, cfg.ContributionBase, cfg.MaxLoanPercentage,
                            cfg.InterestRate, cfg.MinServiceMonthsForLoan))
            cfg.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        this.Flash("PF configuration updated.", "success");
        return Redirect("/pf/");
    }

    // ── Requests by a member ──

    [HttpPost("request-withdrawal")]
    [AnyMember]
    public async Task<IActionResult> RequestWithdrawal(CancellationToken ct)
    {
        double? amount = Py.ParseFloat(Form("amount"));
        string reason = (Form("reason") ?? "").Trim();
        if (amount is not double a || a == 0 || a <= 0 || reason.Length == 0)
        {
            this.Flash("Amount and reason required.", "danger");
            return Redirect("/pf/");
        }
        double balance = await BalanceAsync(Me.User.Id, ct);
        if (a > balance)
        {
            this.Flash($"You can withdraw at most Rs.{Py.Fixed(balance, 0)}.", "danger");
            return Redirect("/pf/");
        }
        db.Add(new PfWithdrawalRequest
        {
            UserId = Me.User.Id, Amount = Py.Dec(a), Reason = reason, Status = "pending", CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Withdrawal request submitted.", "success");
        return Redirect("/pf/");
    }

    [HttpPost("request-loan")]
    [AnyMember]
    public async Task<IActionResult> RequestLoan(CancellationToken ct)
    {
        double? amount = Py.ParseFloat(Form("amount"));
        int? installments = Form("installments") is null ? 12 : Py.ParseInt(Form("installments")) ?? 12;
        string purpose = (Form("purpose") ?? "").Trim();
        if (amount is not double a || a <= 0 || purpose.Length == 0)
        {
            this.Flash("Amount and purpose required.", "danger");
            return Redirect("/pf/");
        }
        if (installments is not int n || n == 0 || !(1 <= n && n <= 60))
        {
            this.Flash("Instalments must be between 1 and 60 months.", "danger");
            return Redirect("/pf/");
        }
        var config = await db.Set<PfConfig>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        double balance = await BalanceAsync(Me.User.Id, ct);
        double maxLoan = config is not null ? balance * (Py.F(config.MaxLoanPercentage) / 100) : balance * 0.5;
        if (a > maxLoan)
        {
            this.Flash($"Loan amount exceeds maximum permissible limit (Rs.{Py.Fixed(maxLoan, 0)}).", "danger");
            return Redirect("/pf/");
        }
        int serviceMonths = 0;
        if (Me.User.DateOfJoining is DateOnly doj)
        {
            var today = clock.Today;
            serviceMonths = (today.Year - doj.Year) * 12 + (today.Month - doj.Month);
        }
        if (config is not null && serviceMonths < (config.MinServiceMonthsForLoan ?? 0))
        {
            this.Flash($"Minimum {config.MinServiceMonthsForLoan} months of service required for loan.", "danger");
            return Redirect("/pf/");
        }
        double monthly = Py.Round(a / n, 2);
        db.Add(new PfLoanRequest
        {
            UserId = Me.User.Id, Amount = Py.Dec(a), InstallmentMonths = n, Purpose = purpose, MonthlyInstallment = Py.Dec(monthly),
            RemainingAmount = Py.Dec(a), Status = "pending", CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Loan request submitted.", "success");
        return Redirect("/pf/");
    }

    [HttpGet("compliance-check/{lid:int}")]
    [AnyMember]
    public async Task<IActionResult> ComplianceCheck(int lid, CancellationToken ct)
    {
        var loan = await db.Set<PfLoanRequest>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid, ct);
        if (loan is null) return NotFound();
        if (loan.UserId != Me.User.Id && !Me.IsAdmin && !Me.IsManager)
            return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        double balance = await BalanceAsync(loan.UserId, ct);
        var config = await db.Set<PfConfig>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        double maxLoan = config is not null ? balance * (Py.F(config.MaxLoanPercentage) / 100) : balance * 0.5;
        var issues = new List<string>();
        if (Py.F(loan.Amount) > maxLoan)
            issues.Add($"Amount exceeds max permissible ({Py.Fixed(maxLoan, 0)})");
        if ((loan.InstallmentMonths ?? 0) > 60)
            issues.Add("Installment period exceeds 60 months");
        return Json(new Dictionary<string, object> { ["compliant"] = issues.Count == 0, ["issues"] = issues });
    }

    // ── Decisions and payments (admin) ──

    [HttpPost("approve-withdrawal/{wid:int}")]
    [AnyMember]
    public async Task<IActionResult> ApproveWithdrawal(int wid, CancellationToken ct)
    {
        if (!Me.IsAdmin) return Reply(false, "Access denied", 403);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Fund movements one at a time per company: two withdrawals approved at once both passed the balance check.
        await db.SerializeAsync("pf", Cid);
        var wr = await db.LockAsync<PfWithdrawalRequest>(wid, ct);
        if (wr is null) return NotFound();
        if (wr.Status != "pending")
            return Reply(false, "This withdrawal has already been decided.", 400);
        if (Form("action") == "reject")
        {
            wr.Status = "rejected";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Reply(true, "Withdrawal rejected.");
        }
        if (Py.F(wr.Amount) > await BalanceAsync(wr.UserId, ct) + 0.005)
            return Reply(false, "The member's PF balance is below this withdrawal.", 400);
        var (account, when, err) = await PaidFromAsync(ct);
        if (err is not null) return err;
        await db.Entry(wr).Reference(w => w.User).LoadAsync(ct);
        var now = clock.UtcNow;
        wr.Status = "approved";
        wr.ApprovedBy = Me.User.Id;
        wr.ApprovedAt = now;
        wr.DisbursedAt = now;
        db.Add(new PfLedger
        {
            UserId = wr.UserId, TransactionDate = when, TransactionType = "withdrawal", Description = "PF withdrawal approved",
            Credit = 0m, Debit = wr.Amount, Balance = 0m, ReferenceId = wr.Id, CreatedAt = now,
        });
        // The fund pays the member: PF Payable comes down with the member's balance.
        await posting.PfCashOutAsync("PF-WD", wr.Id, $"PF-WD-{wr.Id}", Py.F(wr.Amount), account!, when, Me.User.Id,
                                     $"PF withdrawal - {wr.User.FullName}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Reply(true, "Withdrawal approved and paid.");
    }

    [HttpPost("approve-loan/{lid:int}")]
    [AnyMember]
    public async Task<IActionResult> ApproveLoan(int lid, CancellationToken ct)
    {
        if (!Me.IsAdmin) return Reply(false, "Access denied", 403);
        if (!await HasButtonPermAsync("BUTTON_APPROVE_PF_LOAN", ct)) return Reply(false, "No permission to approve loans", 403);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("pf", Cid);
        var loan = await db.LockAsync<PfLoanRequest>(lid, ct);
        if (loan is null) return NotFound();
        if (loan.Status != "pending")
            return Reply(false, "This loan has already been decided.", 400);
        if (Form("action") == "reject")
        {
            loan.Status = "rejected";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Reply(true, "Loan rejected.");
        }
        var (account, when, err) = await PaidFromAsync(ct);
        if (err is not null) return err;
        await db.Entry(loan).Reference(l => l.User).LoadAsync(ct);
        loan.Status = "approved";
        loan.ApprovedBy = Me.User.Id;
        loan.ApprovedAt = clock.UtcNow;
        if (loan.RemainingAmount is null || loan.RemainingAmount == 0)
            loan.RemainingAmount = loan.Amount;
        // The loan is paid out of the member's fund (a DEBIT); payroll recovers the instalments back into it.
        db.Add(new PfLedger
        {
            UserId = loan.UserId, TransactionDate = when, TransactionType = "loan", Description = "PF loan approved",
            Credit = 0m, Debit = loan.Amount, Balance = 0m, ReferenceId = loan.Id, CreatedAt = clock.UtcNow,
        });
        await posting.PfCashOutAsync("PF-LOAN", loan.Id, $"PF-LOAN-{loan.Id}", Py.F(loan.Amount), account!, when, Me.User.Id,
                                     $"PF loan - {loan.User.FullName}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Reply(true, "Loan approved and paid.");
    }

    [HttpPost("distribute-profit")]
    [AnyMember]
    public async Task<IActionResult> DistributeProfit(CancellationToken ct)
    {
        if (!Me.IsAdmin) return Reply(false, "Access denied", 403);
        if (!await HasButtonPermAsync("BUTTON_DISTRIBUTE_PROFIT", ct)) return Reply(false, "No permission", 403);
        int? month = Py.ParseInt(Form("month")), year = Py.ParseInt(Form("year"));
        double? total = Py.ParseFloat(Form("total_profit"));
        if (month is not int m || m == 0 || year is not int y || y == 0 || total is not double profit || profit == 0 || profit <= 0)
            return Reply(false, "Month, year, and total profit required", 400);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("pf", Cid);
        // Shares of the members with a positive balance only.
        var users = (await BalancesAsync(ct)).Where(u => u.Balance > 0).ToList();
        double total0 = Py.Sum(users.Select(u => u.Balance));
        if (total0 <= 0)
            return Reply(false, "No member has a positive PF balance.", 400);
        var dist = new PfProfitDistribution
        {
            Month = m, Year = y, TotalProfit = Py.Dec(profit), DistributedBy = Me.User.Id, DistributedAt = clock.UtcNow,
            Status = "completed",
        };
        db.Add(dist);
        await db.SaveChangesAsync(ct);
        var today = clock.Today;
        await posting.PfProfitAsync(dist, profit, today, Me.User.Id, ct);
        double given = 0.0;
        for (int i = 0; i < users.Count; i++)
        {
            var (uid, bal) = users[i];
            double share = i == users.Count - 1 ? Py.Round(profit - given, 2) : Py.Round(profit * bal / total0, 2);
            given += share;
            db.Add(new PfLedger
            {
                UserId = uid, TransactionDate = today, TransactionType = "profit", Description = $"Profit share {m}/{y}",
                Credit = Py.Dec(share), Debit = 0m, Balance = 0m, CreatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Reply(true, $"Profit of Rs.{Py.Fixed(profit, 0)} distributed.");
    }

    [HttpPost("settle/{uid:int}")]
    [AnyMember]
    public async Task<IActionResult> Settle(int uid, CancellationToken ct)
    {
        if (!Me.IsAdmin) return Reply(false, "Access denied", 403);
        if (!await HasButtonPermAsync("BUTTON_PROCESS_SETTLEMENT", ct)) return Reply(false, "No permission", 403);
        var member = await people.MemberAsync(uid, Me.User.Id, ct);
        if (member is null) return Reply(false, "Not a member of this company.", 404);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A second click (or a second admin) waits here, then finds it settled.
        await db.SerializeAsync("pf", Cid);
        if (await db.Set<PfSettlement>().AnyAsync(s => s.UserId == uid && s.Status == "completed", ct))
            return Reply(false, "This member's PF has already been settled.", 400);
        double employeeContrib = Py.F(await db.Set<PfContribution>().Where(c => c.UserId == uid).SumAsync(c => c.EmployeeAmount, ct) ?? 0m);
        double employerContrib = Py.F(await db.Set<PfContribution>().Where(c => c.UserId == uid).SumAsync(c => c.EmployerAmount, ct) ?? 0m);
        double profitShare = Py.F(await db.Set<PfLedger>().Where(r => r.UserId == uid && r.TransactionType == "profit")
            .SumAsync(r => r.Credit, ct) ?? 0m);
        double outstanding = Py.F(await db.Set<PfLoanRequest>().Where(l => l.UserId == uid && l.Status == "approved")
            .SumAsync(l => l.RemainingAmount, ct) ?? 0m);
        // What is paid out is the ledger balance (contributions and profit in, withdrawals and loans out).
        double net = Py.Round(await BalanceAsync(uid, ct), 2);
        if (net < 0)
            return Reply(false, $"The member owes the fund Rs.{Py.Fixed(-net, 0)}; recover it before settling.", 400);
        ChartOfAccount? account = null;
        DateOnly when = clock.Today;
        if (net != 0)
        {
            var paid = await PaidFromAsync(ct);
            if (paid.Error is not null) return paid.Error;
            (account, when) = (paid.Account, paid.When);
        }
        var now = clock.UtcNow;
        var settlement = new PfSettlement
        {
            UserId = uid, TotalEmployeeContrib = Py.Dec(employeeContrib), TotalEmployerContrib = Py.Dec(employerContrib),
            TotalProfitDistributed = Py.Dec(profitShare), OutstandingLoan = Py.Dec(outstanding), NetSettlement = Py.Dec(net),
            SettledBy = Me.User.Id, SettledAt = now, Status = "completed", CreatedAt = now,
        };
        db.Add(settlement);
        await db.SaveChangesAsync(ct);
        // Any PF loan still open was already netted off the balance paid out.
        foreach (var pfl in await db.Set<PfLoanRequest>().Where(l => l.UserId == uid && l.Status == "approved").ToListAsync(ct))
        {
            pfl.RemainingAmount = 0m;
            pfl.Status = "settled";
        }
        if (net != 0)
        {
            db.Add(new PfLedger
            {
                UserId = uid, TransactionDate = when, TransactionType = "settlement", Description = "Final PF settlement",
                Credit = 0m, Debit = Py.Dec(net), Balance = 0m, ReferenceId = settlement.Id, CreatedAt = now,
            });
            await posting.PfCashOutAsync("PF-SETTLE", settlement.Id, $"PF-SETTLE-{settlement.Id}", net, account!, when, Me.User.Id,
                                         $"Final PF settlement - {member.FullName}", ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Reply(true, $"Settlement paid: Rs.{Py.Fixed(net, 0)}.");
    }

    [HttpPost("button-permissions")]
    [AnyMember]
    public async Task<IActionResult> ButtonPermissions(CancellationToken ct)
    {
        if (!Me.IsAdmin) return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        int? roleId = Py.ParseInt(Form("role_id"));
        string? code = Form("button_code");
        if (roleId is not int rid || string.IsNullOrEmpty(code))
            return new JsonResult(new { error = "All fields required" }) { StatusCode = 400 };
        bool granted = Form("granted") == "true";
        var bp = await db.Set<ButtonPermission>().OrderBy(b => b.Id).FirstOrDefaultAsync(b => b.RoleId == rid && b.ButtonCode == code, ct);
        if (bp is null)
        {
            bp = new ButtonPermission { RoleId = rid, ButtonCode = code, Module = "pf", Description = $"Permission for {code}", IsGranted = false };
            db.Add(bp);
        }
        bp.IsGranted = granted;
        await db.SaveChangesAsync(ct);
        return Json(new Dictionary<string, object> { ["success"] = true });
    }

    [HttpPost("add-ledger-entry")]
    [AnyMember]
    public async Task<IActionResult> AddLedgerEntry(CancellationToken ct)
    {
        if (!Me.IsAdmin) return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        int? userId = Py.ParseInt(Form("user_id"));
        string? type = Form("type");
        string description = (Form("description") ?? "").Trim();
        double? amount = Py.ParseFloat(Form("amount"));
        if (userId is not int uid || uid == 0 || type is not ("credit" or "debit") || amount is not double a || a == 0 || a <= 0)
            return new JsonResult(new { error = "All fields required" }) { StatusCode = 400 };
        if (await people.MemberAsync(uid, Me.User.Id, ct) is null)
            return new JsonResult(new { error = "Not a member of this company" }) { StatusCode = 404 };
        // A manual fund entry needs the other side of the journal, or PF Payable stops agreeing with the members' ledger.
        int counterId = Py.ParseInt(Form("counter_account_id")) ?? 0;
        var counter = await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == counterId, ct);
        if (counter is null || counter.Level < 5)
            return new JsonResult(new { error = "Choose the posting account on the other side" }) { StatusCode = 400 };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entry = new PfLedger
        {
            UserId = uid, TransactionDate = clock.Today, TransactionType = type, Description = description,
            Credit = type == "credit" ? Py.Dec(a) : 0m, Debit = type == "debit" ? Py.Dec(a) : 0m, Balance = 0m, CreatedAt = clock.UtcNow,
        };
        db.Add(entry);
        await db.SaveChangesAsync(ct);
        var fund = await posting.PfPayableAsync(ct);
        var (dr, cr) = type == "credit" ? (counter, fund) : (fund, counter);
        string what = description.Length > 0 ? description : "PF adjustment";
        await ledger.PostAsync("PF-ADJ", entry.Id, $"PF-ADJ-{entry.Id}", description.Length > 0 ? description : "PF ledger adjustment",
        [
            new(dr.Id, Py.Dec(a), 0m, what),
            new(cr.Id, 0m, Py.Dec(a), what),
        ], entry.TransactionDate.ToDateTime(TimeOnly.MinValue), Me.User.Id, ct);
        await tx.CommitAsync(ct);
        return Json(new Dictionary<string, object> { ["success"] = true });
    }
}
