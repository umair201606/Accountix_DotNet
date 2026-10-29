using System.Globalization;
using System.Text.Json;

namespace Accountix.Payroll.Controllers;

public sealed partial class CompensationController
{
    // ── Payroll run: preview, run, replace ──

    [HttpGet("run-payroll")]
    [AnyMember]
    public async Task<IActionResult> RunPayroll(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var now = clock.Now;
        var data = await PayrollData.LoadAsync(db, now.Month, now.Year, ct: ct);
        var employees = PayrollCalculator.PreviewAll(data, Adjustments.None);
        Heading();
        return View("RunPayroll", new RunPayrollModel
        {
            Employees = employees, PfConfig = data.PfConfig, Now = now, NoSlabs = data.Slabs.Count == 0,
        });
    }

    [HttpPost("run-payroll")]
    [AnyMember]
    public async Task<IActionResult> RunPayrollPost(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var f = Request.Form;
        int month = FormInput.Int(f, "month", null), year = FormInput.Int(f, "year", null);
        bool replaceAll = f["replace_all"].FirstOrDefault() == "1";
        string replaceIdsText = f.ContainsKey("replace_ids") ? f["replace_ids"].FirstOrDefault() ?? "" : "[]";
        JsonElement replaceIds;
        try
        {
            replaceIds = string.IsNullOrEmpty(replaceIdsText) ? default : JsonDocument.Parse(replaceIdsText).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new FormInputException("The payroll form could not be read; try again.");
        }
        if (!(1 <= month && month <= 12) || !(2000 <= year && year <= 2100))
        {
            this.Flash("Pick a valid payroll month.", "danger");
            return Redirect("/compensation/run-payroll");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One payroll operation at a time per company (the same queue as the Python app).
        await db.SerializeAsync("payroll", Cid);
        await people.SettingsAsync(ct);
        var adjustments = Adjustments.Parse(f.ContainsKey("adjustments") ? f["adjustments"].FirstOrDefault() : "{}");
        var outcome = await runner.RunAsync(month, year, replaceAll, replaceIds, adjustments, Me.User.Id,
                                            PayrollPeople.RemoteIp(HttpContext), ct);
        if (outcome.Run is not null)
            await tx.CommitAsync(ct);
        else
            await tx.RollbackAsync(ct);
        foreach (var (msg, cat) in outcome.Flashes)
            this.Flash(msg, cat);
        return Redirect(outcome.Redirect);
    }

    [HttpPost("payroll-preview-json")]
    [AnyMember]
    public async Task<IActionResult> PreviewJson(CancellationToken ct)
    {
        if (NotAdminJson() is { } no) return no;
        var f = Request.Form;
        var today = clock.Today;
        int month = FormInput.Int(f, "month", today.Month), year = FormInput.Int(f, "year", today.Year);
        var adjustments = Adjustments.Parse(f.ContainsKey("adjustments") ? f["adjustments"].FirstOrDefault() : "{}");
        var data = await PayrollData.LoadAsync(db, month, year, ct: ct);
        var rows = PayrollCalculator.PreviewAll(data, adjustments).Select(PreviewRow).ToList();
        return Json(new Dictionary<string, object?> { ["employees"] = rows });
    }

    /// <summary>The preview JSON for one employee (the keys _preview_employee returns).</summary>
    internal static Dictionary<string, object?> PreviewRow(EmployeePay p)
    {
        static Dictionary<string, object?> Map<T>(OrderedDictionary<string, T> d) => d.ToDictionary(kv => kv.Key, kv => (object?)Plain(kv.Value));
        static object? Plain(object? v) => v is Py.Num n ? (n.IsInt ? (long)n.Value : n.Value) : v;
        var components = new Dictionary<string, object?> { ["allowances"] = Map(p.Allowances), ["deductions"] = Map(p.Deductions) };
        if (p.Info is not null)
            components["info"] = Map(p.Info);
        return new Dictionary<string, object?>
        {
            ["user_id"] = p.UserId,
            ["name"] = p.Name,
            ["designation"] = p.Designation,
            ["basic"] = p.Basic,
            ["allowances_breakdown"] = Map(p.Allowances),
            ["deductions_breakdown"] = Map(p.Deductions),
            ["components"] = components,
            ["allowances_total"] = p.AllowancesTotal,
            ["bonus"] = p.Bonus,
            ["ot_pay"] = p.OtPay,
            ["gross"] = p.Gross,
            ["taxable"] = p.Taxable,
            ["monthly_tax"] = p.MonthlyTax,
            ["pf_employee"] = p.PfEmployee,
            ["pf_employer"] = p.PfEmployer,
            ["eobi_employee"] = p.EobiEmployee,
            ["settlement"] = Map(p.Settlement),
            ["eobi_employer"] = p.EobiEmployer,
            ["component_deductions"] = p.ComponentDeductions,
            ["loan_deduction"] = p.LoanDeduction,
            ["loans"] = p.Loans.Select(l => new Dictionary<string, object?> { ["id"] = l.Loan.Id, ["amount"] = l.Amount }).ToList(),
            ["pf_loans"] = p.PfLoans.Select(l => new Dictionary<string, object?> { ["id"] = l.Loan.Id, ["amount"] = l.Amount }).ToList(),
            ["pf_loan_deduction"] = p.PfLoanDeduction,
            ["custom_ded"] = p.CustomDeduction,
            ["deductions_total"] = p.DeductionsTotal,
            ["net"] = p.Net,
            ["paid_days"] = p.PaidDays,
            ["unpaid_days"] = Plain(p.UnpaidDays),
            ["active_loans"] = p.RecoverableLoans.Select(l => new Dictionary<string, object?>
            {
                ["id"] = l.Id, ["remaining_amount"] = Py.F(l.RemainingAmount), ["monthly_installment"] = Py.F(l.MonthlyInstallment),
            }).ToList(),
        };
    }

    // ── Bulk data: template and upload ──

    [HttpGet("download-template")]
    [AnyMember]
    public async Task<IActionResult> DownloadTemplate(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var profiles = await db.Set<PayrollProfile>().AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.UserId, p.User.FullName, p.User.IsActive }).ToListAsync(ct);
        var staff = profiles.Where(p => p.IsActive == true).ToList();
        var codes = await people.CodesAsync(staff.Select(s => s.UserId), ct);
        byte[] file = exports.Template(staff.Select(s => (codes[s.UserId], s.FullName)));
        return File(file, PayrollExports.XlsxType, "payroll_template.xlsx");
    }

    [HttpPost("upload-bulk-data")]
    [AnyMember]
    public async Task<IActionResult> UploadBulkData(CancellationToken ct)
    {
        if (NotAdminJson() is { } no) return no;
        var adjustments = new Dictionary<string, Dictionary<string, object?>>();
        if (Request.HasFormContentType && Request.ContentType?.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            var file = Request.Form.Files.GetFile("file");
            if (file is null || string.IsNullOrEmpty(file.FileName))
                return new JsonResult(new { error = "No file uploaded" }) { StatusCode = 400 };
            string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".csv" or ".xlsx"))
                return new JsonResult(new { error = "Upload a .csv or .xlsx file." }) { StatusCode = 400 };
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            List<Dictionary<string, string>> rows;
            try
            {
                rows = PayrollExports.ReadUpload(ms.ToArray(), ext);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new JsonResult(new { error = $"Parse error: {e.Message}" }) { StatusCode = 400 };
            }
            var users = await UsersByCodeAsync(rows.Select(r => (r.GetValueOrDefault("Employee Code") ?? r.GetValueOrDefault("employee_code") ?? "").Trim()), ct);
            foreach (var row in rows)
            {
                string code = (row.TryGetValue("Employee Code", out var c1) ? c1 : row.GetValueOrDefault("employee_code") ?? "").Trim();
                if (!users.TryGetValue(code, out int uid))
                    continue;
                string? Cell(params string[] names)
                {
                    foreach (string n in names)
                        foreach (var (k, v) in row)
                            if (!string.IsNullOrEmpty(k) && k.Trim().ToLowerInvariant().StartsWith(n.ToLowerInvariant(), StringComparison.Ordinal)
                                && v.Trim().Length > 0)
                                return v.Trim();
                    return null;
                }
                var adj = new Dictionary<string, object?>();
                if (Cell("Bonus") is { } bonus) adj["bonus"] = bonus;
                if (Cell("Income Tax", "income_tax", "tax") is { } tax) adj["income_tax"] = tax;
                if (Cell("Custom Deduction", "custom_deduction", "extra_ded") is { } custom) adj["custom_deduction"] = custom;
                if (Cell("Allowances", "allowances_override") is { } allowances) adj["allowances_override"] = allowances;
                if (adj.Count > 0)
                    adjustments[uid.ToString(CultureInfo.InvariantCulture)] = adj;
            }
            string msg = $"Bulk data loaded for {adjustments.Count} employees from {file.FileName}.";
            this.Flash(msg, "success");
            return Json(new Dictionary<string, object?> { ["adjustments"] = adjustments, ["message"] = msg });
        }

        // JSON upload
        JsonElement data = default;
        try
        {
            if (Request.HasJsonContentType())
                data = (await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct)).RootElement.Clone();
        }
        catch (JsonException)
        {
        }
        if (data.ValueKind != JsonValueKind.Object || !data.EnumerateObject().Any())
        {
            this.Flash("Invalid upload data.", "danger");
            return Redirect("/compensation/run-payroll");
        }
        var list = data.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList() : [];
        static object? Raw(JsonElement row, string key) => row.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
        var byCode = await UsersByCodeAsync(list.Select(r => (Raw(r, "employee_code") as string ?? "").Trim()), ct);
        foreach (var row in list)
        {
            string code = (Raw(row, "employee_code") as string ?? "").Trim();
            if (!byCode.TryGetValue(code, out int uid)) continue;
            adjustments[uid.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object?>
            {
                ["bonus"] = Raw(row, "bonus"), ["income_tax"] = Raw(row, "income_tax"), ["custom_deduction"] = Raw(row, "custom_deduction"),
            };
        }
        this.Flash($"Bulk data loaded for {adjustments.Count} employees. Adjustments applied.", "success");
        return Json(new Dictionary<string, object?> { ["adjustments"] = adjustments });
    }

    /// <summary>
    /// _member_user_by_code for a whole sheet: the employee code of an ACTIVE
    /// membership here, else an active account's email that is an active member here.
    /// </summary>
    private async Task<Dictionary<string, int>> UsersByCodeAsync(IEnumerable<string> codes, CancellationToken ct)
    {
        var wanted = codes.Where(c => c.Length > 0).Distinct().ToList();
        var result = new Dictionary<string, int>();
        if (wanted.Count == 0) return result;
        int cid = Cid;
        var byCode = (await db.Memberships.AsNoTracking()
                .Where(m => m.CompanyId == cid && m.Status == CompanyMembership.StatusActive && wanted.Contains(m.EmployeeCode!))
                .OrderBy(m => m.Id).Select(m => new { m.EmployeeCode, m.UserId }).ToListAsync(ct))
            .GroupBy(m => m.EmployeeCode!).ToDictionary(g => g.Key, g => g.First().UserId);
        var rest = wanted.Where(c => !byCode.ContainsKey(c)).ToList();
        var byEmail = rest.Count == 0 ? [] : await db.Users.AsNoTracking()
            .Where(u => rest.Contains(u.Email) && u.IsActive == true
                        && db.Memberships.Any(m => m.CompanyId == cid && m.UserId == u.Id && m.Status == CompanyMembership.StatusActive))
            .Select(u => new { u.Email, u.Id }).ToListAsync(ct);
        foreach (var (k, v) in byCode) result[k] = v;
        foreach (var u in byEmail) result.TryAdd(u.Email, u.Id);
        return result;
    }

    // ── Run detail, payment, delete ──

    [HttpGet("run/{rid:int}")]
    [AnyMember]
    public async Task<IActionResult> ViewRun(int rid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct);
        if (run is null) return NotFound();
        string? processor = run.ProcessedBy is int pb
            ? await db.Users.Where(u => u.Id == pb).Select(u => u.FullName).FirstOrDefaultAsync(ct) : null;
        var slips = await db.Set<PayrollSlip>().AsNoTracking().Where(s => s.PayrollRunId == rid)
            .OrderBy(s => s.User.FullName).ThenBy(s => s.Id)
            .Select(s => new { Slip = s, s.User.FullName, Account = s.User.Designation }).ToListAsync(ct);
        var employment = await people.EmploymentAsync(slips.Select(s => s.Slip.UserId), ct);
        var codes = employment.ToDictionary(kv => kv.Key, kv => kv.Value.EmployeeCode);
        var rows = slips.Select(s => new SlipRow(s.Slip, s.FullName,
            employment.TryGetValue(s.Slip.UserId, out var m) ? m.Designation : s.Account)).ToList();

        // Totals by component across the run, in first-seen order.
        var earnings = new OrderedDictionary<string, double> { ["Basic Salary"] = 0.0 };
        var deductions = new OrderedDictionary<string, double>();
        foreach (var s in rows.Select(r => r.Slip))
        {
            earnings["Basic Salary"] += Py.F(s.BasicSalary);
            var comps = SlipComponents.Parse(s.ComponentsJson);
            foreach (var (name, val) in comps.Allowances)
                earnings[name] = (earnings.TryGetValue(name, out double e) ? e : 0.0) + val;
            foreach (var (name, val) in comps.Deductions)
                deductions[name] = (deductions.TryGetValue(name, out double e) ? e : 0.0) + val;
        }
        // Overtime and bonus are in the slip totals but not always in its breakdown.
        double rest = Py.Round(Py.Sum(rows.Select(r => Py.F(r.Slip.GrossPay))) - Py.Sum(earnings.Values), 2);
        if (rest != 0) earnings["Overtime & bonus"] = rest;
        rest = Py.Round(Py.Sum(rows.Select(r => Py.F(r.Slip.TotalDeductions))) - Py.Sum(deductions.Values), 2);
        if (rest != 0) deductions["Other"] = rest;
        Heading();
        return View("RunDetail", new RunDetailModel
        {
            Run = run, ProcessorName = processor, Slips = rows, Codes = codes, Accounts = await ledger.CashBankAccountsAsync(ct),
            Today = clock.Today,
            Earnings = earnings.Where(kv => Py.Round(kv.Value, 2) != 0).Select(kv => (kv.Key, kv.Value)).ToList(),
            Deductions = deductions.Where(kv => Py.Round(kv.Value, 2) != 0).Select(kv => (kv.Key, kv.Value)).ToList(),
            Period = $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(run.Month)} {run.Year}",
            ShowGlEntry = Me.CanOpenModule(Modules.Accounting),
        });
    }

    [HttpPost("run/{rid:int}/pay")]
    [AnyMember]
    public async Task<IActionResult> PayRun(int rid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("payroll", Cid);
        var run = await db.LockAsync<PayrollRun>(rid, ct);
        if (run is null) return NotFound();
        var account = await people.CashBankAccountAsync(Request.Form["account_id"].FirstOrDefault(), ct)
                      ?? throw new FormInputException("Choose the bank or cash account the salaries were paid from.");
        var when = DateOnly.FromDateTime(DocumentDates.Parse(Request.Form["paid_on"].FirstOrDefault(), null, clock));
        await posting.RecordSalaryPaymentAsync(run, account, when, Me.User.Id, ct);
        db.Add(new PayrollAuditLog
        {
            PayrollRunId = run.Id, Action = "salary_paid", PerformedBy = Me.User.Id,
            Details = $"Paid from {account.Code} on {when:yyyy-MM-dd}", IpAddress = PayrollPeople.RemoteIp(HttpContext),
            PerformedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Salaries of Rs.{Py.Fixed(Py.F(run.TotalNet), 0)} recorded as paid from {account.Name}.", "success");
        return Redirect($"/compensation/run/{run.Id}");
    }

    [HttpPost("run/{rid:int}/unpay")]
    [AnyMember]
    public async Task<IActionResult> UnpayRun(int rid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("payroll", Cid);
        var run = await db.LockAsync<PayrollRun>(rid, ct);
        if (run is null) return NotFound();
        if (run.PaidAt is null)
            throw new FormInputException("This payroll is not marked as paid.");
        await posting.UndoSalaryPaymentAsync(run, ct);
        db.Add(new PayrollAuditLog
        {
            PayrollRunId = run.Id, Action = "salary_payment_undone", PerformedBy = Me.User.Id,
            IpAddress = PayrollPeople.RemoteIp(HttpContext), PerformedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Salary payment reversed.", "success");
        return Redirect($"/compensation/run/{run.Id}");
    }

    [HttpPost("run/{rid:int}/delete")]
    [AnyMember]
    public async Task<IActionResult> DeleteRun(int rid, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("payroll", Cid);
        var run = await db.LockAsync<PayrollRun>(rid, ct);
        if (run is null) return NotFound();
        if (run.PaidAt is not null)
            throw new FormInputException("Undo the salary payment before deleting this payroll.");
        string label = $"{run.Month}/{run.Year}";
        await runner.UndoRunAsync(run, ct);
        db.Add(new PayrollAuditLog
        {
            Action = "payroll_deleted", PerformedBy = Me.User.Id, Details = $"Run for {label} deleted",
            IpAddress = PayrollPeople.RemoteIp(HttpContext), PerformedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Payroll {label} deleted; its journals, loan recoveries and PF entries were reversed.", "success");
        return Redirect("/compensation/");
    }

    // ── Slips and the bank sheet ──

    private async Task<(PayrollSlip Slip, SlipPrint Print)?> SlipAsync(int sid, CancellationToken ct)
    {
        var slip = await db.Set<PayrollSlip>().AsNoTracking().Include(s => s.PayrollRun).Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == sid, ct);
        if (slip is null) return null;
        var employment = (await people.EmploymentAsync([slip.UserId], ct)).GetValueOrDefault(slip.UserId);
        var code = (await people.CodesAsync([slip.UserId], ct))[slip.UserId];
        var print = new SlipPrint
        {
            CompanyName = Me.Company?.Name is { Length: > 0 } n ? n : "Accountix ERP",
            Month = slip.PayrollRun.Month, Year = slip.PayrollRun.Year, EmployeeName = slip.User.FullName,
            Designation = employment is null ? slip.User.Designation : employment.Designation,
            Department = employment is null ? slip.User.Department : employment.Department,
            Code = code, Cnic = slip.User.Cnic,
            BasicSalary = Py.F(slip.BasicSalary), GrossPay = Py.F(slip.GrossPay), TotalDeductions = Py.F(slip.TotalDeductions),
            NetPay = Py.F(slip.NetPay), Components = SlipComponents.Parse(slip.ComponentsJson),
        };
        return (slip, print);
    }

    [HttpGet("slip/{sid:int}")]
    [AnyMember]
    public async Task<IActionResult> ViewSlip(int sid, CancellationToken ct)
    {
        if (await SlipAsync(sid, ct) is not var (slip, print)) return NotFound();
        if (slip.UserId != Me.User.Id && !Me.IsAdmin)
        {
            this.Flash("Access denied.", "danger");
            return Redirect("/dashboard");
        }
        Heading();
        return View("Slip", new SlipModel { Slip = slip, Print = print, CompanyName = Me.Company?.Name });
    }

    [HttpGet("slip/{sid:int}/pdf")]
    [AnyMember]
    public async Task<IActionResult> SlipPdf(int sid, CancellationToken ct)
    {
        if (await SlipAsync(sid, ct) is not var (slip, print)) return NotFound();
        if (slip.UserId != Me.User.Id && !Me.IsAdmin)
        {
            this.Flash("Access denied.", "danger");
            return Redirect("/dashboard");
        }
        string fmt = Me.NumberFormat;
        byte[] pdf = exports.SlipPdf(print, (v, dp) => Py.Amount(v, dp, fmt));
        Response.Headers.ContentDisposition = $"inline; filename=salary_slip_{print.Month}_{print.Year}.pdf";
        return File(pdf, "application/pdf");
    }

    [HttpGet("export-bank-ledger/{rid:int}")]
    [AnyMember]
    public async Task<IActionResult> ExportBankLedger(int rid, CancellationToken ct)
    {
        if (NotAdminJson() is { } no) return no;
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct);
        if (run is null) return NotFound();
        var slips = await db.Set<PayrollSlip>().AsNoTracking().Where(s => s.PayrollRunId == rid).OrderBy(s => s.Id)
            .Select(s => new { s.UserId, s.NetPay, s.User.FullName, s.User.BankName, s.User.BankAccountTitle, s.User.BankAccountNumber })
            .ToListAsync(ct);
        var codes = await people.CodesAsync(slips.Select(s => s.UserId), ct);
        byte[] file = exports.BankSheet(run.Month, run.Year, slips.Select(s => new BankSheetRow(
            codes[s.UserId], s.FullName, s.BankName ?? "", s.BankAccountTitle ?? "", s.BankAccountNumber ?? "", Py.F(s.NetPay))));
        return File(file, PayrollExports.XlsxType, $"bank_ledger_{run.Month}_{run.Year}.xlsx");
    }
}
