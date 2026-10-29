using System.Text.Json;

namespace Accountix.Payroll.Services;

/// <summary>The run screen's per-employee adjustments: {"&lt;user id&gt;": {"bonus": "...", "income_tax": "...", ...}}.</summary>
public sealed class Adjustments
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _byUser;

    private Adjustments(Dictionary<string, Dictionary<string, JsonElement>> byUser) => _byUser = byUser;

    public static Adjustments None { get; } = new([]);

    /// <summary>json.loads(request.form.get("adjustments", "{}")); unreadable text is refused.</summary>
    public static Adjustments Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return None;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, Dictionary<string, JsonElement>>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var u in doc.RootElement.EnumerateObject())
                    if (u.Value.ValueKind == JsonValueKind.Object)
                        map[u.Name] = u.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            return new Adjustments(map);
        }
        catch (JsonException)
        {
            throw new FormInputException("The payroll adjustments could not be read; try again.");
        }
    }

    public static Adjustments From(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values) =>
        Parse(JsonSerializer.Serialize(values));

    public For User(int userId) => new(_byUser.TryGetValue(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var a) ? a : null);

    public readonly struct For(Dictionary<string, JsonElement>? values)
    {
        /// <summary>value in (None, ""): the key missing, JSON null, or exactly "".</summary>
        public bool Blank(string key) =>
            values is null || !values.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null
            || (e.ValueKind == JsonValueKind.String && e.GetString() == "");

        /// <summary>_num(adj.get(key)): blanks and anything unreadable read as 0.</summary>
        public double Num(string key, double fallback = 0.0)
        {
            if (values is null || !values.TryGetValue(key, out var e))
                return fallback;
            switch (e.ValueKind)
            {
                case JsonValueKind.Null:
                    return fallback;
                case JsonValueKind.Number:
                    return e.GetDouble();
                case JsonValueKind.String:
                    string s = e.GetString() ?? "";
                    if (s.Trim().Length == 0) return fallback;
                    return Py.ParseFloat(s.Replace(",", "")) ?? fallback;
                default:
                    return fallback;
            }
        }
    }
}

/// <summary>One employee's pay for the month: what the preview shows, the slip stores and the journal posts.</summary>
public sealed class EmployeePay
{
    public required PayrollProfile Profile { get; init; }
    public int UserId => Profile.UserId;
    public required string Name { get; init; }
    public required string Designation { get; init; }
    public double Basic { get; init; }
    public required OrderedDictionary<string, double> Allowances { get; init; }
    public required OrderedDictionary<string, double> Deductions { get; init; }
    public OrderedDictionary<string, Py.Num>? Info { get; init; }
    public double AllowancesTotal { get; init; }
    public double Bonus { get; init; }
    public double OtPay { get; init; }
    public double Gross { get; init; }
    public double Taxable { get; init; }
    public double MonthlyTax { get; init; }
    public double PfEmployee { get; init; }
    public double PfEmployer { get; init; }
    public double EobiEmployee { get; init; }
    public double EobiEmployer { get; init; }
    public required OrderedDictionary<string, double> Settlement { get; init; }
    public double ComponentDeductions { get; init; }
    public double LoanDeduction { get; init; }
    public required IReadOnlyList<(LoanAdvanceRequest Loan, double Amount)> Loans { get; init; }
    public required IReadOnlyList<(PfLoanRequest Loan, double Amount)> PfLoans { get; init; }
    public double PfLoanDeduction { get; init; }
    public double CustomDeduction { get; init; }
    public double DeductionsTotal { get; init; }
    public double Net { get; init; }
    public double PaidDays { get; init; }
    public Py.Num UnpaidDays { get; init; }
    /// <summary>Paid-out loans being recovered (the preview JSON's active_loans).</summary>
    public required IReadOnlyList<LoanAdvanceRequest> RecoverableLoans { get; init; }
    /// <summary>Every approved loan with a balance (the run page lists them all).</summary>
    public required IReadOnlyList<LoanAdvanceRequest> OpenLoans { get; init; }

    /// <summary>components_json as Python's json.dumps(comps) writes it.</summary>
    public string ComponentsJson()
    {
        var comps = new List<KeyValuePair<string, object?>>
        {
            new("allowances", Allowances.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList()),
            new("deductions", Deductions.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList()),
        };
        if (Info is not null)
            comps.Add(new("info", Info.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList()));
        return Py.Dumps(comps);
    }
}

/// <summary>_preview_employee (hr_app/routes/compensation.py) over a loaded <see cref="PayrollData"/>.</summary>
public static class PayrollCalculator
{
    public static EmployeePay? Preview(PayrollProfile pp, PayrollData d, Adjustments adjustments)
    {
        var adj = adjustments.User(pp.UserId);
        var user = pp.User;
        var left = d.LeavingDate(pp.UserId);
        double unpaidLeave = PayrollMath.UnpaidLeaveDays(d.UnpaidLeave[pp.UserId], d.Month, d.Year);
        var paid = PayrollMath.PaidRatio(user.DateOfJoining, left, unpaidLeave, d.Month, d.Year);
        if (paid is not var (ratio, daysInMonth, unpaidDays))
            return null;
        var revisions = d.Revisions[pp.UserId].ToList();
        double fullBasic = PayrollMath.BasicForMonth(pp.BasicSalary, revisions, d.Month, d.Year);
        double basic = Py.Round(fullBasic * ratio, 2);

        double allowances = 0.0, fullAllowances = 0.0;
        double taxableRegular = basic, fullTaxable = fullBasic;
        var allow = new OrderedDictionary<string, double>();
        var ded = new OrderedDictionary<string, double>();
        double componentDeductions = 0.0;
        bool overrideAllowances = !adj.Blank("allowances_override");
        foreach (var c in d.Components[pp.Id])
        {
            double full = PayrollMath.ComponentAmount(c.CalculationMethod, c.Value, fullBasic);
            double val = Py.Round(full * ratio, 2);
            if (c.Type == "allowance")
            {
                if (!overrideAllowances)
                {
                    allowances += val;
                    fullAllowances += full;
                    allow[c.Name] = val;
                    if (c.IsTaxable == true)
                    {
                        taxableRegular += val;
                        fullTaxable += full;
                    }
                }
            }
            else
            {
                // Statutory / fixed deductions are a flat monthly amount, not prorated.
                componentDeductions += full;
                ded[c.Name] = full;
            }
        }
        if (overrideAllowances)
        {
            allowances = adj.Num("allowances_override");
            taxableRegular = basic + allowances;
            fullTaxable = fullBasic + allowances;
            allow["Allowances (as entered)"] = Py.Round(allowances, 2);
        }

        double otHours = d.OvertimeHours.GetValueOrDefault(pp.UserId);
        double otPay = PayrollMath.OvertimePay(d.Policy, otHours, fullBasic);
        double bonus = adj.Num("bonus");
        if (otPay != 0) allow["Overtime"] = otPay;
        if (bonus != 0) allow["Bonus"] = Py.Round(bonus, 2);
        var settlement = PayrollMath.FinalSettlement(d.Policy, user.DateOfJoining, left, d.Month, d.Year, fullBasic,
                                                     fullBasic + fullAllowances, d.CarryForwardRemaining[pp.UserId]);
        foreach (var (name, amt) in settlement)
            allow[name] = amt;
        double oneOff = otPay + bonus + Py.Sum(settlement.Values);
        allowances += oneOff;
        double gross = Py.Round(basic + allowances, 2);
        double taxable = Py.Round(taxableRegular + oneOff, 2);

        double monthlyTax = !adj.Blank("income_tax")
            ? Py.Round(adj.Num("income_tax"), 2)
            : PayrollMath.Withholding(d.Slabs, d.Policy, PayrollMath.YearToDate(d.YtdSlips[pp.UserId], d.Month, d.Year),
                                      d.Month, d.Year, taxableRegular, oneOff, fullTaxable);

        double pfEmployee = 0.0, pfEmployer = 0.0;
        var cfg = d.PfConfig;
        if (cfg is not null && cfg.IsActive != false)
        {
            double pfb = PayrollMath.PfBase(cfg.ContributionBase, basic, gross);
            pfEmployee = Py.Round(pfb * Py.F(cfg.EmployeeContributionPct) / 100, 2);
            pfEmployer = Py.Round(pfb * Py.F(cfg.EmployerContributionPct) / 100, 2);
        }
        var (eobiEmployee, eobiEmployer) = PayrollMath.Eobi(d.Policy, ratio);
        double customDed = Py.Round(adj.Num("custom_deduction"), 2);

        // Loans are recovered from what is left after everything else, never beyond it.
        double available = gross - monthlyTax - pfEmployee - eobiEmployee - componentDeductions - customDed;
        var loans = new List<(LoanAdvanceRequest, double)>();
        double loanDeduction = 0.0;
        // Only loans actually paid out are recovered.
        var recoverable = d.Loans[pp.UserId].Where(l => l.DisbursedAt is not null).ToList();
        foreach (var loan in recoverable)
        {
            double remaining = Py.F(loan.RemainingAmount);
            if (remaining <= 0) continue;
            string k = $"loan_{loan.Id}";
            double due = !adj.Blank(k) ? adj.Num(k) : Due(loan.MonthlyInstallment, loan.Amount, loan.InstallmentMonths);
            double take = Py.Round(Py.Max(0.0, Py.Min(due, remaining, available - loanDeduction)), 2);
            if (take > 0)
            {
                loans.Add((loan, take));
                loanDeduction += take;
            }
        }
        loanDeduction = Py.Round(loanDeduction, 2);
        // PF loans: repaid into the fund, after everything else.
        var pfLoans = new List<(PfLoanRequest, double)>();
        double pfLoanDeduction = 0.0;
        foreach (var pfl in d.PfLoans[pp.UserId])
        {
            double remaining = Py.F(pfl.RemainingAmount);
            string k = $"pfloan_{pfl.Id}";
            double due = !adj.Blank(k) ? adj.Num(k) : Due(pfl.MonthlyInstallment, pfl.Amount, pfl.InstallmentMonths);
            double take = Py.Round(Py.Max(0.0, Py.Min(due, remaining, available - loanDeduction - pfLoanDeduction)), 2);
            if (take > 0)
            {
                pfLoans.Add((pfl, take));
                pfLoanDeduction += take;
            }
        }
        pfLoanDeduction = Py.Round(pfLoanDeduction, 2);

        double deductions = Py.Round(componentDeductions + monthlyTax + pfEmployee + eobiEmployee
                                     + loanDeduction + pfLoanDeduction + customDed, 2);
        double net = Py.Round(gross - deductions, 2);
        ded["Income Tax"] = Py.Round(monthlyTax, 2);
        if (cfg is not null) ded["PF Employee"] = Py.Round(pfEmployee, 2);
        if (eobiEmployee != 0) ded["EOBI (employee)"] = eobiEmployee;
        if (loanDeduction > 0) ded["Loan Repayment"] = loanDeduction;
        if (pfLoanDeduction > 0) ded["PF Loan Repayment"] = pfLoanDeduction;
        if (customDed != 0) ded["Additional Deduction"] = customDed;
        OrderedDictionary<string, Py.Num>? info = null;
        if (unpaidDays.Value != 0 || ratio < 1)
            info = new OrderedDictionary<string, Py.Num>
            {
                ["Days in month"] = Py.Num.Int(daysInMonth),
                ["Paid days"] = Py.Round(daysInMonth * ratio, 2),
                ["Unpaid leave days"] = unpaidDays,
            };
        return new EmployeePay
        {
            Profile = pp,
            Name = user.FullName,
            Designation = d.DesignationOf(user) ?? "",
            Basic = basic,
            Allowances = allow,
            Deductions = ded,
            Info = info,
            AllowancesTotal = Py.Round(allowances, 2),
            Bonus = Py.Round(bonus, 2),
            OtPay = otPay,
            Gross = gross,
            Taxable = taxable,
            MonthlyTax = monthlyTax,
            PfEmployee = pfEmployee,
            PfEmployer = pfEmployer,
            EobiEmployee = eobiEmployee,
            EobiEmployer = eobiEmployer,
            Settlement = settlement,
            ComponentDeductions = Py.Round(componentDeductions, 2),
            LoanDeduction = loanDeduction,
            Loans = loans,
            PfLoans = pfLoans,
            PfLoanDeduction = pfLoanDeduction,
            CustomDeduction = customDed,
            DeductionsTotal = deductions,
            Net = net,
            PaidDays = Py.Round(daysInMonth * ratio, 2),
            UnpaidDays = unpaidDays,
            RecoverableLoans = recoverable,
            OpenLoans = d.Loans[pp.UserId].ToList(),
        };
    }

    /// <summary>float(monthly_installment or 0) or float(amount or 0) / max(installment_months or 1, 1).</summary>
    public static double Due(decimal? monthlyInstallment, decimal amount, int? months)
    {
        double mi = Py.F(monthlyInstallment);
        if (mi != 0) return mi;
        int m = months is int x && x != 0 ? x : 1;
        return Py.F(amount) / Math.Max(m, 1);
    }

    /// <summary>Everyone in this month's payroll, in profile order (in_payroll, then a pay for the month).</summary>
    public static List<EmployeePay> PreviewAll(PayrollData d, Adjustments adjustments) =>
        d.Profiles
            .Where(pp => PayrollMath.InPayroll(pp.User.IsActive == true, d.LeavingDate(pp.UserId), d.Month, d.Year))
            .Select(pp => Preview(pp, d, adjustments))
            .OfType<EmployeePay>()
            .ToList();
}
