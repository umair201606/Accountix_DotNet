using Accountix.Payroll.Services;

namespace Accountix.Payroll.Models;

public sealed record ProfileRow(PayrollProfile Profile, string FullName, int ComponentCount);

public sealed class CompensationIndexModel
{
    public IReadOnlyList<ProfileRow> Profiles { get; init; } = [];
    public IReadOnlyList<PayrollRun> Runs { get; init; } = [];
    public IReadOnlyList<IncomeTaxSlab> Slabs { get; init; } = [];
}

public sealed class EmployeeCompensationModel
{
    public PayrollProfile? Profile { get; init; }
    public IReadOnlyList<PayrollSlip> Slips { get; init; } = [];
}

public sealed class TaxSettingsModel
{
    public IReadOnlyList<IncomeTaxSlab> Slabs { get; init; } = [];
    public required PayrollSetting Policy { get; init; }
}

public sealed record EmployeeOption(int Id, string FullName, string Email);

public sealed class CreateProfileModel
{
    public IReadOnlyList<EmployeeOption> Employees { get; init; } = [];
}

public sealed class EditProfileModel
{
    public PayrollProfile? Profile { get; init; }
    public IReadOnlyList<PayrollComponent> Components { get; init; } = [];
    public required User Employee { get; init; }
}

public sealed class RunPayrollModel
{
    public IReadOnlyList<EmployeePay> Employees { get; init; } = [];
    public PfConfig? PfConfig { get; init; }
    public DateTime Now { get; init; }
    public bool NoSlabs { get; init; }
}

public sealed record SlipRow(PayrollSlip Slip, string FullName, string? Designation);

public sealed class RunDetailModel
{
    public required PayrollRun Run { get; init; }
    public string? ProcessorName { get; init; }
    public IReadOnlyList<SlipRow> Slips { get; init; } = [];
    public IReadOnlyDictionary<int, string?> Codes { get; init; } = new Dictionary<int, string?>();
    public IReadOnlyList<ChartOfAccount> Accounts { get; init; } = [];
    public DateOnly Today { get; init; }
    public IReadOnlyList<(string Name, double Value)> Earnings { get; init; } = [];
    public IReadOnlyList<(string Name, double Value)> Deductions { get; init; } = [];
    public required string Period { get; init; }
    public bool ShowGlEntry { get; init; }
}

public sealed record LoanRow(LoanAdvanceRequest Loan, string? FullName);

public sealed class LoansModel
{
    public IReadOnlyList<LoanRow> Loans { get; init; } = [];
    public IReadOnlyList<ChartOfAccount> Accounts { get; init; } = [];
    public DateOnly Today { get; init; }
}

public sealed class SlipModel
{
    public required PayrollSlip Slip { get; init; }
    public required SlipPrint Print { get; init; }
    public string? CompanyName { get; init; }
}

public sealed record RevisionView(SalaryRevision Revision, string FullName, string? ApproverName);

public sealed class RevisionsModel
{
    public IReadOnlyList<RevisionView> Revisions { get; init; } = [];
}

public sealed record NamedRow<T>(T Row, string FullName);

public sealed class PfAdminModel
{
    public PfConfig? Config { get; init; }
    public IReadOnlyList<NamedRow<PfContribution>> Contributions { get; init; } = [];
    public IReadOnlyList<NamedRow<PfWithdrawalRequest>> Withdrawals { get; init; } = [];
    public IReadOnlyList<NamedRow<PfLoanRequest>> Loans { get; init; } = [];
    public IReadOnlyList<ChartOfAccount> Accounts { get; init; } = [];
    public DateOnly Today { get; init; }
    public IReadOnlyList<(int UserId, string FullName, double Balance)> Members { get; init; } = [];
    public bool CanCalc { get; init; }
    public bool CanProfit { get; init; }
    public bool CanSettle { get; init; }
}

public sealed class PfEmployeeModel
{
    public PfConfig? Config { get; init; }
    public IReadOnlyList<PfContribution> Contributions { get; init; } = [];
    public IReadOnlyList<PfLedger> Ledger { get; init; } = [];
    public double Balance { get; init; }
    public IReadOnlyList<PfWithdrawalRequest> Withdrawals { get; init; } = [];
    public IReadOnlyList<PfLoanRequest> Loans { get; init; } = [];
}

public sealed class PfConfigModel
{
    public required PfConfig Config { get; init; }
}
