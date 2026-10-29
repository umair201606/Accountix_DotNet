using System.Text.Json;

namespace Accountix.Payroll.Services;

/// <summary>What a run asked the page to show and where to go next.</summary>
public sealed class RunOutcome
{
    public List<(string Message, string Category)> Flashes { get; } = [];
    public required string Redirect { get; set; }
    public PayrollRun? Run { get; set; }
}

/// <summary>
/// Running, replacing and deleting a payroll month (run_payroll, _undo_run in
/// hr_app/routes/compensation.py). The caller holds the transaction and the
/// per-company "payroll" queue.
/// </summary>
public sealed class PayrollRunner(AccountixDbContext db, ILedger ledger, CompanyClock clock)
{
    /// <summary>
    /// _undo_run: take a run fully back out (journals, staff-loan and PF-loan
    /// recoveries, PF rows, slips, the run), saved but not committed. A closed
    /// period refuses.
    /// </summary>
    public async Task UndoRunAsync(PayrollRun run, CancellationToken ct = default)
    {
        await ledger.ReverseAsync("PRL", run.Id, ct: ct);
        var repayments = await db.Set<LoanRepayment>().Where(r => r.PayrollRunId == run.Id).OrderBy(r => r.Id).ToListAsync(ct);
        var loanIds = repayments.Select(r => r.LoanId).Distinct().ToList();
        var loans = await db.Set<LoanAdvanceRequest>().Where(l => loanIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        foreach (var lr in repayments)
        {
            if (loans.TryGetValue(lr.LoanId, out var loan))
            {
                double rem = Py.Round(Py.F(loan.RemainingAmount) + Py.F(lr.Amount), 2);
                loan.RemainingAmount = Py.Dec(rem);
                if (loan.Status == "paid" && rem > 0)
                    loan.Status = "approved";
            }
            db.Remove(lr);
        }
        var pfRows = await db.Set<PfLedger>().Where(r => r.PayrollRunId == run.Id).OrderBy(r => r.Id).ToListAsync(ct);
        var pfLoanIds = pfRows.Where(r => r.TransactionType == "loan_repayment" && r.ReferenceId is int x && x != 0)
            .Select(r => r.ReferenceId!.Value).Distinct().ToList();
        var pfLoans = await db.Set<PfLoanRequest>().Where(l => pfLoanIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        foreach (var row in pfRows)
        {
            if (row.TransactionType == "loan_repayment" && row.ReferenceId is int rid && rid != 0
                && pfLoans.TryGetValue(rid, out var pfl))
            {
                pfl.RemainingAmount = Py.Dec(Py.Round(Py.F(pfl.RemainingAmount) + Py.F(row.Credit), 2));
                if (pfl.Status == "repaid")
                    pfl.Status = "approved";
            }
            db.Remove(row);
        }
        await db.SaveChangesAsync(ct);
        await db.Set<PfContribution>().Where(c => c.Month == run.Month && c.Year == run.Year).ExecuteDeleteAsync(ct);
        // Contribution rows written before runs were linked to them.
        string legacy = $"PF contribution {run.Month}/{run.Year}";
        await db.Set<PfLedger>().Where(r => r.Description == legacy && r.PayrollRunId == null).ExecuteDeleteAsync(ct);
        await db.Set<PayrollSlip>().Where(s => s.PayrollRunId == run.Id).ExecuteDeleteAsync(ct);
        // SQLAlchemy nulls the audit rows' link when their run is deleted (no delete cascade).
        await db.Set<PayrollAuditLog>().Where(a => a.PayrollRunId == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PayrollRunId, (int?)null), ct);
        foreach (var slip in db.ChangeTracker.Entries<PayrollSlip>().Where(e => e.Entity.PayrollRunId == run.Id).ToList())
            slip.State = EntityState.Detached;
        db.Remove(run);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// run_payroll (POST), after the month was validated and the per-company queue taken.
    /// Saves everything; the caller commits when <see cref="RunOutcome.Run"/> is set.
    /// </summary>
    public async Task<RunOutcome> RunAsync(int month, int year, bool replaceAll, JsonElement replaceIds, Adjustments adjustments,
                                           int userId, string? ip, CancellationToken ct = default)
    {
        var outcome = new RunOutcome { Redirect = "/compensation/run-payroll" };
        var (_, periodEnd) = PayrollMath.MonthBounds(month, year);
        var existing = await db.Set<PayrollRun>().Where(r => r.Month == month && r.Year == year).OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (!replaceAll)
            {
                outcome.Flashes.Add(($"Payroll already run for {month}/{year}. Use 'Replace All' to override.", "danger"));
                return outcome;
            }
            if (existing.PaidAt is not null)
            {
                outcome.Flashes.Add(($"Payroll {month}/{year} is already paid. Undo the salary " +
                                     "payment on the run page before replacing it.", "danger"));
                outcome.Redirect = $"/compensation/run/{existing.Id}";
                return outcome;
            }
            // Saved, not committed: if the new run is refused below, the rollback keeps the old one.
            await UndoRunAsync(existing, ct);
            outcome.Flashes.Add(($"Replaced existing payroll for {month}/{year}.", "info"));
        }
        var data = await PayrollData.LoadAsync(db, month, year, tracked: true, ct);
        if (data.Profiles.Count == 0)
        {
            outcome.Flashes.Add(("No payroll profiles found.", "danger"));
            outcome.Redirect = "/compensation/";
            return outcome;
        }
        var pfConfig = data.PfConfig;
        var wanted = replaceIds.ValueKind == JsonValueKind.Array
            ? replaceIds.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet()
            : [];
        bool filter = replaceIds.ValueKind == JsonValueKind.Array ? replaceIds.GetArrayLength() > 0
            : replaceIds.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False);

        var pays = new List<EmployeePay>();
        foreach (var pp in data.Profiles)
        {
            if (!PayrollMath.InPayroll(pp.User.IsActive == true, data.LeavingDate(pp.UserId), month, year))
                continue;
            if (filter && !wanted.Contains(pp.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)) && !replaceAll)
                continue;
            var pay = PayrollCalculator.Preview(pp, data, adjustments);
            if (pay is null)
                continue;
            if (pay.Net < 0)
            {
                outcome.Flashes.Add(($"{pay.Name}: deductions ({Py.Fixed(pay.DeductionsTotal, 2)}) exceed pay " +
                                     $"({Py.Fixed(pay.Gross, 2)}) for {month}/{year}. Adjust the deductions and run again.", "danger"));
                outcome.Redirect = "/compensation/run-payroll";
                return outcome;     // the caller does not commit: everything rolls back
            }
            pays.Add(pay);
        }
        var pr = new PayrollRun
        {
            Month = month, Year = year, ProcessedBy = userId, Status = "processing", RunDate = clock.UtcNow,
            TotalGross = 0m, TotalDeductions = 0m, TotalNet = 0m, EmployeeCount = 0,
        };
        db.Add(pr);
        await db.SaveChangesAsync(ct);

        double totalGross = 0, totalDed = 0, totalNet = 0, totalTax = 0, totalPfEe = 0, totalPfEr = 0, totalLoan = 0;
        double totalCustom = 0, totalOther = 0, totalEobiEe = 0, totalEobiEr = 0, totalPfLoan = 0;
        int empCount = 0;
        var settlementTotals = new OrderedDictionary<string, double>();
        var loanCredits = new SortedDictionary<int, (LoanAdvanceRequest Loan, double Amount)>();
        var now = clock.UtcNow;
        string period = $"{month}/{year}";

        foreach (var pay in pays)
        {
            var pp = pay.Profile;
            double pfEmployer = pfConfig is not null ? pay.PfEmployer : 0;
            // The loans and amounts the preview recovered: the same figures, not a second calculation.
            foreach (var (loan, amt) in pay.Loans)
            {
                double rem = Py.Round(Py.F(loan.RemainingAmount) - amt, 2);
                loan.RemainingAmount = Py.Dec(rem);
                if (rem <= 0)
                    loan.Status = "paid";
                db.Add(new LoanRepayment
                {
                    LoanId = loan.Id, Amount = Py.Dec(amt), PayrollRunId = pr.Id, PaidAt = now,
                    Notes = $"Deducted via payroll {period}",
                });
                loanCredits[loan.Id] = (loan, (loanCredits.TryGetValue(loan.Id, out var prev) ? prev.Amount : 0) + amt);
            }
            // PF loans are repaid into the member's fund.
            foreach (var (pfl, amt) in pay.PfLoans)
            {
                double rem = Py.Round(Py.F(pfl.RemainingAmount) - amt, 2);
                pfl.RemainingAmount = Py.Dec(rem);
                if (rem <= 0)
                    pfl.Status = "repaid";
                db.Add(new PfLedger
                {
                    UserId = pp.UserId, TransactionDate = periodEnd, TransactionType = "loan_repayment",
                    Description = $"PF loan #{pfl.Id} repayment {period}", Credit = Py.Dec(amt), Debit = 0m,
                    Balance = 0m, ReferenceId = pfl.Id, PayrollRunId = pr.Id, CreatedAt = now,
                });
                totalPfLoan += amt;
            }
            db.Add(new PayrollSlip
            {
                PayrollRunId = pr.Id, UserId = pp.UserId, BasicSalary = Py.Dec(pay.Basic),
                Allowances = Py.Dec(pay.AllowancesTotal), Deductions = Py.Dec(pay.DeductionsTotal),
                GrossPay = Py.Dec(pay.Gross), TotalDeductions = Py.Dec(pay.DeductionsTotal), NetPay = Py.Dec(pay.Net),
                TaxablePay = Py.Dec(pay.Taxable), ComponentsJson = pay.ComponentsJson(), CreatedAt = now,
            });
            totalGross += pay.Gross; totalDed += pay.DeductionsTotal; totalNet += pay.Net; empCount++;
            totalTax += pay.MonthlyTax;
            totalPfEe += pay.PfEmployee;
            totalPfEr += pfEmployer;
            totalLoan += pay.LoanDeduction;
            totalCustom += pay.CustomDeduction;
            totalEobiEe += pay.EobiEmployee;
            foreach (var (name, amt) in pay.Settlement)
                settlementTotals[name] = (settlementTotals.TryGetValue(name, out double s) ? s : 0) + amt;
            totalEobiEr += pay.EobiEmployer;
            // Deduction-type components: whatever the slip deducted that is not tax / PF / EOBI / loan / custom.
            totalOther += Py.Round(pay.DeductionsTotal - pay.MonthlyTax - pay.PfEmployee - pay.EobiEmployee - pay.LoanDeduction
                                   - pay.CustomDeduction - pay.PfLoanDeduction, 2);
            if (pfConfig is not null)
            {
                double both = Py.Round(pay.PfEmployee + pfEmployer, 2);
                db.Add(new PfContribution
                {
                    UserId = pp.UserId, Month = month, Year = year, EmployeeAmount = Py.Dec(pay.PfEmployee),
                    EmployerAmount = Py.Dec(pfEmployer), TotalAmount = Py.Dec(both), CreatedAt = now,
                });
                db.Add(new PfLedger
                {
                    UserId = pp.UserId, TransactionDate = periodEnd, TransactionType = "contribution",
                    Description = $"PF contribution {period}", Credit = Py.Dec(both), Debit = 0m, Balance = 0m,
                    PayrollRunId = pr.Id, CreatedAt = now,
                });
            }
            Notify(pp.UserId, "Salary Slip Generated", $"Your salary slip for {period} is available.", userId, now);
            if (data.ManagerOf(pp.User) is int manager && manager != 0)
                Notify(manager, $"Salary Slip: {pp.User.FullName}", $"Salary for {period}: Rs.{Py.Fixed(pay.Net, 0)}", userId, now);
        }

        pr.Status = "completed";
        pr.TotalGross = Py.Dec(Py.Round(totalGross, 2));
        pr.TotalDeductions = Py.Dec(Py.Round(totalDed, 2));
        pr.TotalNet = Py.Dec(Py.Round(totalNet, 2));
        pr.EmployeeCount = empCount;
        await db.SaveChangesAsync(ct);
        // The slips, PF rows and notifications are written: stop tracking them, so the
        // ledger's account lookups and saves below do not scan a thousand rows each time.
        db.ChangeTracker.Clear();

        // Post the salary journal (accounts fetched in Python's order: new ones are numbered in it).
        var salaryExp = await ledger.GetOrCreateAccountAsync("5121", "Salary Expense", "expense", "512", ct);
        var salaryPayable = await ledger.GetOrCreateAccountAsync("2121", "Salary Payable", "liability", "212", ct);
        var taxPayable = await ledger.GetOrCreateAccountAsync("2122", "Income Tax Payable", "liability", "212", ct);
        var pfPayable = await ledger.GetOrCreateAccountAsync("2123", "PF Payable", "liability", "212", ct);
        var loanClearing = await ledger.GetOrCreateAccountAsync("2124", "Loan Deductions Clearing", "liability", "212", ct);
        var pfExp = await ledger.GetOrCreateAccountAsync("5122", "PF Employer Expense", "expense", "512", ct);
        var otherPayable = await ledger.GetOrCreateAccountAsync("2-01-02-02-0004", "Other Payroll Deductions Payable", "liability", null, ct);
        var eobiPayable = await ledger.GetOrCreateAccountAsync("2125", "EOBI Payable", "liability", "212", ct);
        var eobiExp = await ledger.GetOrCreateAccountAsync("5123", "EOBI Employer Expense", "expense", "512", ct);
        // Final settlements are their own expenses, not salary.
        var settleAccounts = new Dictionary<string, ChartOfAccount>
        {
            ["Gratuity"] = await ledger.GetOrCreateAccountAsync("5124", "Gratuity Expense", "expense", "512", ct),
            ["Leave encashment"] = await ledger.GetOrCreateAccountAsync("5125", "Leave Encashment Expense", "expense", "512", ct),
        };
        double settled = Py.Round(Py.Sum(settlementTotals.Values), 2);
        var lines = new List<JournalLineInput>
        {
            new(salaryExp.Id, Py.Dec(Py.Round(totalGross - settled, 2)), 0m, $"Gross salary {period}"),
        };
        foreach (var (name, amt) in settlementTotals.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (Py.Round(amt, 2) > 0)
                lines.Add(new(settleAccounts[name].Id, Py.Dec(Py.Round(amt, 2)), 0m, $"{name} on leaving {period}"));
        if (Py.Round(totalTax, 2) > 0)
            lines.Add(new(taxPayable.Id, 0m, Py.Dec(Py.Round(totalTax, 2)), "Income tax deducted"));
        if (Py.Round(totalPfEe, 2) > 0)
            lines.Add(new(pfPayable.Id, 0m, Py.Dec(Py.Round(totalPfEe, 2)), "PF employee contribution"));
        if (Py.Round(totalPfLoan, 2) > 0)
            lines.Add(new(pfPayable.Id, 0m, Py.Dec(Py.Round(totalPfLoan, 2)), "PF loan repayments"));
        // Each loan is recovered against its own ledger account. The accounts that already
        // exist under their names are read in one query (create_entity_account's code:
        // the loan parent's code + id + 100); only new or renamed ones go through the ledger.
        var loanCodes = loanCredits.Keys.ToDictionary(id => id, LoanAccountCode);
        var known = await db.Set<ChartOfAccount>().Where(a => loanCodes.Values.Contains(a.Code)).ToDictionaryAsync(a => a.Code, ct);
        double recovered = 0;
        foreach (var (loanId, (loan, amount)) in loanCredits)
        {
            double amt = Py.Round(amount, 2);
            if (amt <= 0) continue;
            string name = PayrollPosting.LoanAccountName(loan);
            var acct = known.TryGetValue(loanCodes[loanId], out var k) && k.Name == name && k.Level == 5
                ? k
                : await ledger.EntityAccountAsync("loan", loanId, name, ct);
            lines.Add(new(acct.Id, 0m, Py.Dec(amt), $"Loan #{loanId} instalment"));
            recovered += amt;
        }
        if (Math.Abs(Py.Round(totalLoan - recovered, 2)) >= 0.01)
            lines.Add(new(loanClearing.Id, 0m, Py.Dec(Py.Round(totalLoan - recovered, 2)), "Loan repayment deductions"));
        // Component and custom deductions are withheld from staff, so they are not owed to them.
        if (Py.Round(totalOther + totalCustom, 2) > 0)
            lines.Add(new(otherPayable.Id, 0m, Py.Dec(Py.Round(totalOther + totalCustom, 2)), "Other payroll deductions"));
        if (Py.Round(totalEobiEe, 2) > 0)
            lines.Add(new(eobiPayable.Id, 0m, Py.Dec(Py.Round(totalEobiEe, 2)), "EOBI employee contribution"));
        lines.Add(new(salaryPayable.Id, 0m, Py.Dec(Py.Round(totalNet, 2)), "Net salary payable"));
        var entryDate = periodEnd.ToDateTime(TimeOnly.MinValue);
        string ym = $"{year}{month:00}";
        await ledger.PostAsync("PRL", pr.Id, $"PRL-{ym}", $"Payroll Run {period}", lines, entryDate, userId, ct);

        if (Py.Round(totalPfEr, 2) > 0)
            await ledger.PostAsync("PRL", pr.Id, $"PRL-{ym}-PF", $"PF Employer Contribution {period}",
            [
                new(pfExp.Id, Py.Dec(Py.Round(totalPfEr, 2)), 0m, $"PF employer contribution {period}"),
                new(pfPayable.Id, 0m, Py.Dec(Py.Round(totalPfEr, 2)), "PF employer contribution"),
            ], entryDate, userId, ct);
        if (Py.Round(totalEobiEr, 2) > 0)
            await ledger.PostAsync("PRL", pr.Id, $"PRL-{ym}-EOBI", $"EOBI Employer Contribution {period}",
            [
                new(eobiExp.Id, Py.Dec(Py.Round(totalEobiEr, 2)), 0m, $"EOBI employer {period}"),
                new(eobiPayable.Id, 0m, Py.Dec(Py.Round(totalEobiEr, 2)), "EOBI employer contribution"),
            ], entryDate, userId, ct);

        db.Add(new PayrollAuditLog
        {
            PayrollRunId = pr.Id, Action = "payroll_run", PerformedBy = userId, Details = $"Run for {period}",
            IpAddress = ip, PerformedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        outcome.Run = pr;
        outcome.Flashes.Add(($"Payroll completed for {empCount} employees. Net: Rs.{Py.Fixed(totalNet, 0)}", "success"));
        outcome.Redirect = $"/compensation/run/{pr.Id}";
        return outcome;
    }

    /// <summary>shared/coa.py ENTITY_PARENT_CODES["loan"] + "-" + (id + ENTITY_ID_OFFSET), as create_entity_account codes it.</summary>
    public static string LoanAccountCode(int loanId) =>
        $"1-01-03-01-{(loanId + 100).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(4, '0')}";

    private void Notify(int recipient, string title, string message, int createdBy, DateTime now)
    {
        var n = new Notification
        {
            Title = title, Message = message, NotificationType = "info", Module = "compensation",
            CreatedBy = createdBy, CreatedAt = now,
        };
        n.NotificationRecipients.Add(new NotificationRecipient { UserId = recipient, IsRead = false });
        db.Add(n);
    }
}
