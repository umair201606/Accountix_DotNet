using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accountix.Payroll.Controllers;

/// <summary>
/// Compensation (hr_app/routes/compensation.py): salary packages, tax and payroll
/// settings, payroll runs, slips, the bank sheet and staff loans. HR pages are
/// governed by role in the routes (admin, or the slip's own employee), as in Python.
/// </summary>
[Area("Payroll")]
[Module(Modules.Hr)]
[Route("compensation")]
public sealed partial class CompensationController(AccountixDbContext db, PayrollPeople people, PayrollPosting posting,
                                                   PayrollRunner runner, PayrollExports exports, ILedger ledger,
                                                   CompanyClock clock) : Controller
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private EffectiveAccess Me => HttpContext.Access()!;
    private int Cid => Me.CompanyId!.Value;

    /// <summary>The HR routes' refusal: flash "Access denied." and go to the dashboard (or <paramref name="to"/>).</summary>
    private IActionResult? NotAdmin(string to = "/dashboard")
    {
        if (Me.IsAdmin) return null;
        this.Flash("Access denied.", "danger");
        return Redirect(to);
    }

    private IActionResult? NotAdminJson() =>
        Me.IsAdmin ? null : new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };

    private void Heading() => ViewData["Heading"] = "Accountix ERP";

    // ── Index ──

    [HttpGet("")]
    [AnyMember]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Heading();
        if (Me.IsAdmin)
        {
            var profiles = await db.Set<PayrollProfile>().AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new ProfileRow(p, p.User.FullName, p.PayrollComponents.Count)).ToListAsync(ct);
            var runs = await db.Set<PayrollRun>().AsNoTracking().OrderByDescending(r => r.RunDate).ThenByDescending(r => r.Id)
                .Take(12).ToListAsync(ct);
            var slabs = await db.Set<IncomeTaxSlab>().AsNoTracking().Where(s => s.IsActive == true)
                .OrderBy(s => s.MinIncome).ThenBy(s => s.Id).ToListAsync(ct);
            return View("Index", new CompensationIndexModel { Profiles = profiles, Runs = runs, Slabs = slabs });
        }
        int me = Me.User.Id;
        var profile = await db.Set<PayrollProfile>().AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(p => p.UserId == me, ct);
        var slips = await db.Set<PayrollSlip>().AsNoTracking().Include(s => s.PayrollRun).Where(s => s.UserId == me)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).ToListAsync(ct);
        return View("Employee", new EmployeeCompensationModel { Profile = profile, Slips = slips });
    }

    // ── Income tax slabs and payroll settings ──

    [HttpGet("tax-settings")]
    [AnyMember]
    public async Task<IActionResult> TaxSettings(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var slabs = await db.Set<IncomeTaxSlab>().AsNoTracking().Where(s => s.IsActive == true)
            .OrderBy(s => s.MinIncome).ThenBy(s => s.Id).ToListAsync(ct);
        var policy = await people.SettingsAsync(ct);
        await tx.CommitAsync(ct);
        Heading();
        return View("TaxSettings", new TaxSettingsModel { Slabs = slabs, Policy = policy });
    }

    [HttpPost("tax-settings")]
    [AnyMember]
    public async Task<IActionResult> TaxSettingsPost(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var f = Request.Form;
        string? action = f["action"].FirstOrDefault();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (action == "add")
        {
            double min = FormInput.Float(f, "min_income", null), max = FormInput.Float(f, "max_income", null);
            double rate = FormInput.Float(f, "rate_pct", null), fixedAmount = FormInput.Float(f, "fixed_amount", 0);
            if (min < 0 || max <= min || !(0 <= rate && rate <= 100) || fixedAmount < 0)
                throw new FormInputException("A slab needs 0 <= from < to, a rate between 0 and 100% and a fixed amount of zero or more.");
            db.Add(new IncomeTaxSlab
            {
                MinIncome = Py.Dec(min), MaxIncome = Py.Dec(max), RatePct = Py.Dec(rate), FixedAmount = Py.Dec(fixedAmount),
                IsActive = true, CreatedAt = clock.UtcNow,
            });
            this.Flash("Tax slab added.", "success");
        }
        else if (action == "delete")
        {
            int id = FormInput.Int(f, "slab_id", null);
            var slab = await db.Set<IncomeTaxSlab>().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (slab is null) return NotFound();
            db.Remove(slab);
            this.Flash("Tax slab deleted.", "success");
        }
        else if (action == "seed_defaults")
        {
            // Replaces the active set: mixing in a previous year's rows gave a table no year ever had.
            await db.Set<IncomeTaxSlab>().Where(s => s.IsActive == true)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
            foreach (var (lo, hi, rate, fixedAmount) in PayrollMath.SlabsTy2026)
                db.Add(new IncomeTaxSlab
                {
                    MinIncome = (decimal)lo, MaxIncome = (decimal)hi, RatePct = (decimal)rate, FixedAmount = (decimal)fixedAmount,
                    IsActive = true, CreatedAt = clock.UtcNow,
                });
            this.Flash("Tax year 2026 salaried slabs loaded (previous slabs deactivated).", "success");
        }
        else if (action == "policy")
        {
            var s = await people.SettingsAsync(ct);
            double mult = FormInput.Float(f, "overtime_multiplier", 1.5, "Overtime rate");
            double hours = FormInput.Float(f, "overtime_hours_basis", 160, "Hours a month");
            double wageBase = FormInput.Float(f, "eobi_wage_base", 0, "EOBI wage base");
            double ee = FormInput.Float(f, "eobi_employee_pct", 0, "EOBI employee %");
            double er = FormInput.Float(f, "eobi_employer_pct", 0, "EOBI employer %");
            double thr = FormInput.Float(f, "surcharge_threshold", 0, "Surcharge threshold");
            double sur = FormInput.Float(f, "surcharge_pct", 0, "Surcharge %");
            if (!(1 <= mult && mult <= 5) || !(1 <= hours && hours <= 400) || wageBase < 0 || !(0 <= ee && ee <= 100)
                || !(0 <= er && er <= 100) || thr < 0 || !(0 <= sur && sur <= 100))
                throw new FormInputException("Overtime rate 1–5x, hours 1–400, percentages 0–100 and amounts of zero or more.");
            s.OvertimeMultiplier = Py.Dec(mult);
            s.OvertimeHoursBasis = Py.Dec(hours);
            s.EobiEnabled = f["eobi_enabled"].FirstOrDefault() == "1";
            s.EobiWageBase = Py.Dec(wageBase);
            s.EobiEmployeePct = Py.Dec(ee);
            s.EobiEmployerPct = Py.Dec(er);
            s.SurchargeThreshold = Py.Dec(thr);
            s.SurchargePct = Py.Dec(sur);
            double days = FormInput.Float(f, "gratuity_days_per_year", 30, "Gratuity days a year");
            if (!(0 <= days && days <= 365))
                throw new FormInputException("Gratuity days a year must be between 0 and 365.");
            s.GratuityEnabled = f["gratuity_enabled"].FirstOrDefault() == "1";
            s.GratuityBasis = f["gratuity_basis"].FirstOrDefault() == "gross" ? "gross" : "basic";
            s.GratuityDaysPerYear = Py.Dec(days);
            s.LeaveEncashmentEnabled = f["leave_encashment_enabled"].FirstOrDefault() == "1";
            this.Flash("Payroll settings saved.", "success");
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Redirect("/compensation/tax-settings");
    }

    // ── Payroll profiles (salary packages) ──

    [HttpGet("create-profile")]
    [AnyMember]
    public async Task<IActionResult> CreateProfile(CancellationToken ct)
    {
        if (NotAdmin("/compensation/") is { } no) return no;
        var employees = await people.EmployeesAsync(ct);
        Heading();
        return View("CreateProfile", new CreateProfileModel { Employees = employees.Select(e => new EmployeeOption(e.Id, e.FullName, e.Email)).ToList() });
    }

    [HttpPost("create-profile")]
    [AnyMember]
    public async Task<IActionResult> CreateProfilePost(CancellationToken ct)
    {
        if (NotAdmin("/compensation/") is { } no) return no;
        var f = Request.Form;
        int? uid = Py.ParseInt(f["user_id"].FirstOrDefault());
        double? basic = Py.ParseFloat(f["basic_salary"].FirstOrDefault());
        string? effective = f["effective_from"].FirstOrDefault();
        if (uid is not int u || u == 0 || basic is not double b || b == 0 || string.IsNullOrEmpty(effective))
        {
            this.Flash("All fields required.", "danger");
            return Redirect("/compensation/create-profile");
        }
        var member = await people.MemberAsync(u, Me.User.Id, ct);
        if (b <= 0 || member is null)
        {
            this.Flash("Pick an employee of this company and a basic salary above zero.", "danger");
            return Redirect("/compensation/create-profile");
        }
        if (await db.Set<PayrollProfile>().AnyAsync(p => p.UserId == u, ct))
        {
            this.Flash("Profile already exists for this employee.", "warning");
            return Redirect($"/compensation/edit-profile/{u}");
        }
        if (StrpDate(effective) is not DateOnly eff)
        {
            this.Flash("Invalid date format.", "danger");
            return Redirect("/compensation/create-profile");
        }
        var now = clock.UtcNow;
        var profile = new PayrollProfile
        {
            UserId = u, BasicSalary = Py.Dec(b), EffectiveFrom = eff, PaymentMethod = "bank_transfer", CreatedAt = now, UpdatedAt = now,
        };
        // Basic is the profile's own figure (gross = basic + allowances): no "Basic Salary" allowance.
        // Starter allowances as a percentage of basic, so they follow every revision.
        foreach (var (name, pct, taxable) in new[] { ("House Rent", 25, true), ("Medical Allowance", 10, false), ("Conveyance", 10, true) })
            profile.PayrollComponents.Add(new PayrollComponent
            {
                Name = name, Type = "allowance", CalculationMethod = "percent_of_basic", Value = pct, IsTaxable = taxable,
            });
        db.Add(profile);
        await db.SaveChangesAsync(ct);
        this.Flash($"Profile created for {member.FullName}.", "success");
        return Redirect($"/compensation/edit-profile/{u}");
    }

    /// <summary>datetime.strptime(value, "%Y-%m-%d") (one-digit month and day allowed).</summary>
    internal static DateOnly? StrpDate(string? value)
    {
        if (value is null) return null;
        var m = Regex.Match(value, @"^(\d{4})-(\d{1,2})-(\d{1,2})$");
        if (!m.Success) return null;
        int y = int.Parse(m.Groups[1].Value, Inv), mo = int.Parse(m.Groups[2].Value, Inv), d = int.Parse(m.Groups[3].Value, Inv);
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateOnly(y, mo, d);
    }

    [HttpGet("edit-profile/{uid:int}")]
    [AnyMember]
    public async Task<IActionResult> EditProfile(int uid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var profile = await db.Set<PayrollProfile>().AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(p => p.UserId == uid, ct);
        var emp = await people.MemberAsync(uid, Me.User.Id, ct);
        if (emp is null) return NotFound();
        var comps = profile is null ? [] : await db.Set<PayrollComponent>().AsNoTracking()
            .Where(c => c.ProfileId == profile.Id).OrderBy(c => c.Id).ToListAsync(ct);
        Heading();
        return View("EditProfile", new EditProfileModel { Profile = profile, Components = comps, Employee = emp });
    }

    private sealed record CleanComponent(string Name, string Type, string Method, double Value, bool Taxable);

    [HttpPost("edit-profile/{uid:int}")]
    [AnyMember]
    public async Task<IActionResult> EditProfilePost(int uid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var emp = await people.MemberAsync(uid, Me.User.Id, ct);
        if (emp is null) return NotFound();
        var f = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<PayrollProfile>().OrderBy(p => p.Id).FirstOrDefaultAsync(p => p.UserId == uid, ct);
        bool created = false;
        if (profile is null)
        {
            var now = clock.UtcNow;
            profile = new PayrollProfile
            {
                UserId = uid, BasicSalary = 0m, EffectiveFrom = clock.Today, PaymentMethod = "bank_transfer", CreatedAt = now, UpdatedAt = now,
            };
            db.Add(profile);
            await db.SaveChangesAsync(ct);
            created = true;
        }
        double basic = FormInput.Float(f, "basic_salary", null, "Basic salary");
        if (basic <= 0)
            throw new FormInputException("Basic salary must be more than zero.");
        var clean = ReadComponents(f["components_json"].FirstOrDefault());
        var effective = FormInput.Date(f, "effective_from");
        double oldBasic = Py.F(profile.BasicSalary);
        if (oldBasic != 0 && Py.Round(basic, 2) != Py.Round(oldBasic, 2))
        {
            // A raise is a dated revision: payroll pays each month at the rate in force then.
            var when = effective != profile.EffectiveFrom ? effective : clock.Today;
            db.Add(new SalaryRevision
            {
                UserId = uid, PreviousBasic = Py.Dec(oldBasic), NewBasic = Py.Dec(basic), Reason = "Salary package edited",
                ApprovedBy = Me.User.Id, EffectiveFrom = when, CreatedAt = clock.UtcNow,
            });
            effective = when;
        }
        decimal newBasic = Py.Dec(basic);
        if (created || profile.BasicSalary != newBasic || profile.EffectiveFrom != effective)
            profile.UpdatedAt = clock.UtcNow;
        profile.BasicSalary = newBasic;
        profile.EffectiveFrom = effective;
        var old = await db.Set<PayrollComponent>().Where(c => c.ProfileId == profile.Id).ToListAsync(ct);
        db.RemoveRange(old);
        foreach (var c in clean)
            db.Add(new PayrollComponent
            {
                ProfileId = profile.Id, Name = c.Name, Type = c.Type, CalculationMethod = c.Method, Value = Py.Dec(c.Value),
                IsTaxable = c.Type == "allowance" && c.Taxable,
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Salary package updated for {emp.FullName}.", "success");
        return Redirect("/compensation/");
    }

    /// <summary>The salary-package form's components_json, validated as Python does.</summary>
    private static List<CleanComponent> ReadComponents(string? raw)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrEmpty(raw) ? "[]" : raw);
        }
        catch (JsonException)
        {
            throw new FormInputException("The salary components could not be read; try again.");
        }
        using (doc)
        {
            var clean = new List<CleanComponent>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return clean;
            foreach (var comp in doc.RootElement.EnumerateArray())
            {
                if (comp.ValueKind != JsonValueKind.Object)
                    throw new FormInputException("The salary components could not be read; try again.");
                string name = Truthy(comp, "name") ? Str(comp.GetProperty("name")).Trim() : "";
                if (name.Length == 0)
                    continue;
                string? type = comp.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                               && t.GetString() is "allowance" or "deduction" ? t.GetString() : null;
                string? method = Truthy(comp, "method")
                    ? comp.GetProperty("method").ValueKind == JsonValueKind.String ? comp.GetProperty("method").GetString() : "?"
                    : "fixed";
                if (type is null || method is not ("fixed" or "percent_of_basic"))
                    throw new FormInputException($"{name}: choose allowance/deduction and fixed/% of basic.");
                double value = 0;
                if (Truthy(comp, "value"))
                    value = Py.JsonNumber(comp.GetProperty("value")) ?? throw new FormInputException($"{name}: the amount must be a number.");
                if (value < 0 || (method == "percent_of_basic" && value > 100))
                    throw new FormInputException($"{name}: amount must be zero or more (a percentage at most 100).");
                bool taxable = !comp.TryGetProperty("is_taxable", out _) || Truthy(comp, "is_taxable");
                clean.Add(new CleanComponent(name, type, method, value, taxable));
            }
            return clean;
        }
    }

    private static bool Truthy(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => v.GetString()!.Length > 0,
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => v.EnumerateObject().Any(),
            _ => true,
        };
    }

    private static string Str(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

    // ── Salary revisions ──

    [HttpGet("revisions")]
    [AnyMember]
    public async Task<IActionResult> Revisions(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var rows = await db.Set<SalaryRevision>().AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => new RevisionView(r, r.User.FullName, r.ApprovedByNavigation != null ? r.ApprovedByNavigation.FullName : null))
            .ToListAsync(ct);
        Heading();
        return View("Revisions", new RevisionsModel { Revisions = rows });
    }

    // ── Staff loans & advances (admin) ──

    [HttpGet("loans")]
    [AnyMember]
    public async Task<IActionResult> Loans(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var rows = await db.Set<LoanAdvanceRequest>().AsNoTracking().OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
            .Select(l => new LoanRow(l, l.User.FullName)).ToListAsync(ct);
        Heading();
        return View("Loans", new LoansModel { Loans = rows, Accounts = await ledger.CashBankAccountsAsync(ct), Today = clock.Today });
    }

    [HttpPost("loans/{lid:int}/disburse")]
    [AnyMember]
    public async Task<IActionResult> DisburseLoan(int lid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await db.LockAsync<LoanAdvanceRequest>(lid, ct);
        if (loan is null) return NotFound();
        await db.Entry(loan).Reference(l => l.User).LoadAsync(ct);
        var account = await people.CashBankAccountAsync(Request.Form["account_id"].FirstOrDefault(), ct)
                      ?? throw new FormInputException("Choose the bank or cash account the loan is paid from.");
        var when = DocumentDates.Parse(Request.Form["paid_on"].FirstOrDefault(), null, clock);
        await posting.DisburseLoanAsync(loan, account, DateOnly.FromDateTime(when), Me.User.Id, ct);
        var n = new Notification
        {
            Title = "Loan Paid",
            Message = $"Your {loan.RequestType} of Rs.{Py.Fixed(Py.F(loan.Amount), 0)} has been paid. Instalments start with the next payroll.",
            NotificationType = "success", Module = "compensation", CreatedBy = Me.User.Id, CreatedAt = clock.UtcNow,
        };
        n.NotificationRecipients.Add(new NotificationRecipient { UserId = loan.UserId, IsRead = false });
        db.Add(n);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{Py.Title(loan.RequestType)} #{loan.Id} paid out from {account.Name}.", "success");
        return Redirect("/compensation/loans");
    }

    [HttpPost("loans/{lid:int}/undo-disbursement")]
    [AnyMember]
    public async Task<IActionResult> UndoLoanDisbursement(int lid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await db.LockAsync<LoanAdvanceRequest>(lid, ct);
        if (loan is null) return NotFound();
        await posting.UndoDisbursementAsync(loan, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Loan payment reversed.", "success");
        return Redirect("/compensation/loans");
    }
}
