namespace Accountix.Payroll.Services;

/// <summary>
/// The cash side of payroll in the general ledger (hr_app/payroll_posting.py):
/// salary payment, staff-loan pay-out, money paid out of the provident fund and
/// PF profit. Each posting is keyed (voucher type, document id) so undoing it is
/// one reversal. Refusals use Python's words (raised as FormInputError there).
/// </summary>
public sealed class PayrollPosting(AccountixDbContext db, ILedger ledger, CompanyClock clock)
{
    public Task<ChartOfAccount> SalaryPayableAsync(CancellationToken ct = default) =>
        ledger.GetOrCreateAccountAsync("2121", "Salary Payable", "liability", "212", ct);

    public Task<ChartOfAccount> PfPayableAsync(CancellationToken ct = default) =>
        ledger.GetOrCreateAccountAsync("2123", "PF Payable", "liability", "212", ct);

    public Task<ChartOfAccount> PfProfitExpenseAsync(CancellationToken ct = default) =>
        ledger.GetOrCreateAccountAsync("5126", "PF Profit Credited to Members", "expense", "512", ct);

    public static string LoanAccountName(LoanAdvanceRequest loan) =>
        $"{Py.Title(loan.RequestType)} #{loan.Id} - {loan.User.FullName}";

    public Task<ChartOfAccount> LoanAccountAsync(LoanAdvanceRequest loan, CancellationToken ct = default) =>
        ledger.EntityAccountAsync("loan", loan.Id, LoanAccountName(loan), ct);

    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    /// <summary>record_salary_payment: Dr Salary Payable (net) / Cr the bank or cash account.</summary>
    public async Task RecordSalaryPaymentAsync(PayrollRun run, ChartOfAccount account, DateOnly when, int userId, CancellationToken ct = default)
    {
        if (run.PaidAt is not null)
            throw new FormInputException("This payroll has already been paid.");
        double net = Py.Round(Py.F(run.TotalNet), 2);
        if (net <= 0)
            throw new FormInputException("Nothing to pay on this payroll.");
        string period = $"{run.Month}/{run.Year}";
        var payable = await SalaryPayableAsync(ct);
        await ledger.PostAsync("PRL-PAY", run.Id, $"PRL-PAY-{run.Year}{run.Month:00}", $"Salaries paid {period}",
        [
            new(payable.Id, Py.Dec(net), 0m, $"Net salaries {period}"),
            new(account.Id, 0m, Py.Dec(net), $"Salaries paid {period}"),
        ], Day(when), userId, ct);
        run.PaidAt = clock.UtcNow;
        run.Status = "paid";
    }

    public async Task UndoSalaryPaymentAsync(PayrollRun run, CancellationToken ct = default)
    {
        await ledger.ReverseAsync("PRL-PAY", run.Id, ct: ct);
        run.PaidAt = null;
        run.Status = "completed";
    }

    /// <summary>disburse_loan: Dr the employee's loan account / Cr the bank or cash account.</summary>
    public async Task DisburseLoanAsync(LoanAdvanceRequest loan, ChartOfAccount account, DateOnly when, int userId, CancellationToken ct = default)
    {
        if (loan.Status != "approved")
            throw new FormInputException("Only an approved loan can be paid out.");
        if (loan.DisbursedAt is not null)
            throw new FormInputException("This loan has already been paid out.");
        double amount = Py.Round(Py.F(loan.Amount), 2);
        string kind = Py.Title(loan.RequestType);
        var loanAccount = await LoanAccountAsync(loan, ct);
        await ledger.PostAsync("LOAN-DSB", loan.Id, $"LOAN-DSB-{loan.Id}", $"{kind} paid to {loan.User.FullName}",
        [
            new(loanAccount.Id, Py.Dec(amount), 0m, $"{kind} #{loan.Id}"),
            new(account.Id, 0m, Py.Dec(amount), $"Paid to {loan.User.FullName}"),
        ], Day(when), userId, ct);
        loan.DisbursedAt = when.ToDateTime(new TimeOnly(12, 0));
    }

    public async Task UndoDisbursementAsync(LoanAdvanceRequest loan, CancellationToken ct = default)
    {
        if (await db.Set<LoanRepayment>().AnyAsync(r => r.LoanId == loan.Id, ct))
            throw new FormInputException("Instalments have already been recovered on this loan.");
        await ledger.ReverseAsync("LOAN-DSB", loan.Id, ct: ct);
        loan.DisbursedAt = null;
    }

    /// <summary>pf_cash_out: money paid out of the fund, Dr PF Payable / Cr cash or bank. Nothing for a zero amount.</summary>
    public async Task<JournalEntry?> PfCashOutAsync(string voucherType, int docId, string number, double amount, ChartOfAccount account,
                                                    DateOnly when, int userId, string description, CancellationToken ct = default)
    {
        amount = Py.Round(amount, 2);
        if (amount <= 0)
            return null;
        var fund = await PfPayableAsync(ct);
        return await ledger.PostAsync(voucherType, docId, number, description,
        [
            new(fund.Id, Py.Dec(amount), 0m, description),
            new(account.Id, 0m, Py.Dec(amount), description),
        ], Day(when), userId, ct);
    }

    /// <summary>pf_profit: Dr PF Profit Credited to Members / Cr PF Payable.</summary>
    public async Task<JournalEntry> PfProfitAsync(PfProfitDistribution dist, double totalProfit, DateOnly when, int userId,
                                                  CancellationToken ct = default)
    {
        double amount = Py.Round(totalProfit, 2);
        var expense = await PfProfitExpenseAsync(ct);
        var fund = await PfPayableAsync(ct);
        return await ledger.PostAsync("PF-PROFIT", dist.Id, $"PF-PROFIT-{dist.Year}{dist.Month:00}-{dist.Id}",
            $"PF profit credited {dist.Month}/{dist.Year}",
        [
            new(expense.Id, Py.Dec(amount), 0m, $"PF profit {dist.Month}/{dist.Year}"),
            new(fund.Id, 0m, Py.Dec(amount), "Profit credited to members"),
        ], Day(when), userId, ct);
    }
}
