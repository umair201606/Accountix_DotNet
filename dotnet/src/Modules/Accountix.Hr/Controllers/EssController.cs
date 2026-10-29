using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>Employee self-service (hr_app/routes/ess.py), avatar and loan requests included.</summary>
[Area("Hr")]
[Module(Modules.Hr)]
[Route("ess")]
public sealed class EssController(AccountixDbContext db, CompanyClock clock, LeaveFlow flow, People people, Notify notify,
                                  FileStore files, ILedger ledger) : HrController
{
    public sealed record SlipRow(PayrollSlip Slip, int Month, int Year);

    public sealed class IndexModel
    {
        public required User User { get; init; }
        public IReadOnlyList<(LeaveQuota Quota, string Type)> Quotas { get; init; } = [];
        public IReadOnlyList<LoanAdvanceRequest> Loans { get; init; } = [];
        public IReadOnlyList<SlipRow> Slips { get; init; } = [];
        public IReadOnlyList<PerformanceReview> Reviews { get; init; } = [];
    }

    private Task<List<SlipRow>> SlipsAsync(int? take, CancellationToken ct)
    {
        var q = from s in db.Set<PayrollSlip>().AsNoTracking()
                where s.UserId == Me
                join r in db.Set<PayrollRun>() on s.PayrollRunId equals r.Id
                orderby s.CreatedAt descending
                select new SlipRow(s, r.Month, r.Year);
        return (take is int n ? q.Take(n) : q).ToListAsync(ct);
    }

    [HttpGet("")]
    [AnyMember]   // self service
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        int year = DateTime.Today.Year;       // Python: date.today()
        var types = await db.Set<LeaveType>().OrderBy(t => t.Id).ToListAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var lt in types)
                await flow.RefreshAsync(await flow.QuotaForAsync(Me, lt, year, ct), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        var names = types.ToDictionary(t => t.Id, t => t.Name);
        var quotas = await db.Set<LeaveQuota>().AsNoTracking().Where(q => q.UserId == Me && q.Year == year).OrderBy(q => q.Id).ToListAsync(ct);
        return View(new IndexModel
        {
            User = await db.Users.AsNoTracking().FirstAsync(u => u.Id == Me, ct),
            Quotas = quotas.Select(q => (q, names.GetValueOrDefault(q.LeaveTypeId, ""))).ToList(),
            Loans = await db.Set<LoanAdvanceRequest>().AsNoTracking().Where(l => l.UserId == Me).OrderByDescending(l => l.CreatedAt).ToListAsync(ct),
            Slips = await SlipsAsync(12, ct),
            Reviews = await db.Set<PerformanceReview>().AsNoTracking().Where(r => r.UserId == Me).OrderByDescending(r => r.CreatedAt).ToListAsync(ct),
        });
    }

    /// <summary>The profile fields an employee asks HR to change (ChangeRequest.field_name).</summary>
    private static readonly string[] Requestable =
        ["phone", "emergency_contact", "emergency_phone", "address", "bank_name", "bank_account_title", "bank_account_number"];

    internal static string? Get(User u, string field) => field switch
    {
        "phone" => u.Phone, "emergency_contact" => u.EmergencyContact, "emergency_phone" => u.EmergencyPhone,
        "address" => u.Address, "bank_name" => u.BankName, "bank_account_title" => u.BankAccountTitle,
        "bank_account_number" => u.BankAccountNumber, _ => null,
    };

    private static void Set(User u, string field, string value)
    {
        switch (field)
        {
            case "phone": u.Phone = value; break;
            case "emergency_contact": u.EmergencyContact = value; break;
            case "emergency_phone": u.EmergencyPhone = value; break;
            case "address": u.Address = value; break;
            case "bank_name": u.BankName = value; break;
            case "bank_account_title": u.BankAccountTitle = value; break;
            case "bank_account_number": u.BankAccountNumber = value; break;
        }
    }

    [HttpPost("update-profile")]
    [AnyMember]
    public async Task<IActionResult> UpdateProfile(CancellationToken ct)
    {
        var me = await db.Users.FirstAsync(u => u.Id == Me, ct);
        string name = FormStr("full_name").Trim();
        if (name.Length > 0 && name != me.FullName)
        {
            me.FullName = name;          // the display name changes directly
            me.UpdatedAt = clock.UtcNow;
        }
        foreach (string f in Requestable)
        {
            string val = FormStr(f).Trim();
            if (val.Length > 0 && Get(me, f) != val
                && !await db.Set<ChangeRequest>().AnyAsync(c => c.UserId == Me && c.FieldName == f && c.Status == "pending", ct))
                db.Add(new ChangeRequest
                {
                    UserId = Me, FieldName = f, OldValue = Get(me, f) ?? "", NewValue = val, Status = "pending", CreatedAt = clock.UtcNow,
                });
        }
        await db.SaveChangesAsync(ct);
        this.Flash("Profile updated.", "success");
        return Redirect("/ess/");
    }

    private static readonly HashSet<string> ImageTypes = ["png", "jpg", "jpeg", "gif", "webp"];

    private static string AvatarKey(string filename) => $"avatars/{filename}";

    [HttpPost("upload-picture")]
    [AnyMember]
    public async Task<IActionResult> UploadPicture(CancellationToken ct)
    {
        var file = Request.Form.Files.GetFile("profile_picture");
        if (file is null || string.IsNullOrEmpty(file.FileName))
        {
            this.Flash("No file selected.", "danger");
            return Redirect("/ess/");
        }
        int dot = file.FileName.LastIndexOf('.');
        string ext = dot < 0 ? "" : file.FileName[(dot + 1)..].ToLowerInvariant();
        if (dot < 0 || !ImageTypes.Contains(ext))
        {
            this.Flash("Allowed image types: PNG, JPG, JPEG, GIF, WebP.", "danger");
            return Redirect("/ess/");
        }
        string filename = $"avatar_{Me}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{ext}";
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var me = await db.Users.FirstAsync(u => u.Id == Me, ct);
        await files.SaveAsync(AvatarKey(filename), ms.ToArray(), file.ContentType, ct);
        if (!string.IsNullOrEmpty(me.ProfileImage) && me.ProfileImage != filename)
            await files.DeleteAsync(AvatarKey(me.ProfileImage), ct: ct);
        me.ProfileImage = filename;
        me.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        this.Flash("Profile picture updated.", "success");
        return Redirect("/ess/");
    }

    public sealed record ChangeRow(ChangeRequest Request, string Employee);

    [HttpGet("change-requests")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> ChangeRequests(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        var rows = await (from c in db.Set<ChangeRequest>().AsNoTracking()
                          where c.Status == "pending"
                          join u in db.Users on c.UserId equals u.Id
                          orderby c.CreatedAt descending
                          select new ChangeRow(c, u.FullName)).ToListAsync(ct);
        ViewData["BackUrl"] = "/ess/";
        return View(rows);
    }

    [HttpPost("review-change/{cid:int}")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> ReviewChange(int cid, CancellationToken ct)
    {
        if (!IsAdmin) return JsonStatus(new { error = "Access denied" }, 403);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var cr = await db.LockAsync<ChangeRequest>(cid, ct);
        if (cr is null) return NotFound();
        string? action = Request.Form["action"].FirstOrDefault();
        string notes = FormStr("notes");
        if (cr.Status != "pending")
        {
            this.Flash("This change request has already been reviewed.", "danger");
            return Redirect("/ess/change-requests");
        }
        if (action == "approve")
        {
            var user = await people.GetMemberAsync(cr.UserId, Access, ct);
            if (user is null)
            {
                this.Flash("That person is no longer a member of this company.", "danger");
                return Redirect("/ess/change-requests");
            }
            Set(user, cr.FieldName, cr.NewValue);
            user.UpdatedAt = clock.UtcNow;
            cr.Status = "approved";
        }
        else
            cr.Status = "rejected";
        cr.ReviewedBy = Me;
        cr.ReviewNotes = notes;
        cr.ReviewedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Change request {Py.Decided(action)}.", "success");
        return Redirect("/ess/change-requests");
    }

    [HttpGet("loans")]
    [AnyMember]
    public async Task<IActionResult> Loans(CancellationToken ct)
    {
        ViewData["BackUrl"] = "/ess/";
        return View(await db.Set<LoanAdvanceRequest>().AsNoTracking().Where(l => l.UserId == Me)
            .OrderByDescending(l => l.CreatedAt).ToListAsync(ct));
    }

    [HttpPost("loans")]
    [AnyMember]
    public async Task<IActionResult> RequestLoan(CancellationToken ct)
    {
        double amount;
        int months;
        string rawAmount = FormStr("amount"), rawMonths = FormStr("installments");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool okAmount = double.TryParse(rawAmount.Length == 0 ? "0" : rawAmount.Trim(), System.Globalization.NumberStyles.Float, inv, out amount);
        bool okMonths = int.TryParse(rawMonths.Length == 0 ? "12" : rawMonths.Trim(), System.Globalization.NumberStyles.Integer, inv, out months);
        if (!okAmount || !okMonths || double.IsNaN(amount)) { amount = 0; months = 0; }
        amount = Py.R(amount, 2);
        string purpose = FormStr("purpose").Trim();
        string rtype = FormStr("type", "loan");
        if (amount <= 0 || !(1 <= months && months <= 120) || purpose.Length == 0 || rtype is not ("loan" or "advance"))
        {
            this.Flash("Enter an amount above zero, 1–120 instalments and a purpose.", "danger");
            return Redirect("/ess/loans");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == Me, ct);
        var loan = new LoanAdvanceRequest
        {
            UserId = Me, RequestType = rtype, Amount = (decimal)amount, Purpose = purpose, InstallmentMonths = months,
            MonthlyInstallment = (decimal)Py.R(amount / months, 2), RemainingAmount = (decimal)amount,
            Status = "pending", QueueForPayroll = false, CreatedAt = clock.UtcNow,
        };
        db.Add(loan);
        await db.SaveChangesAsync(ct);
        await ledger.EntityAccountAsync("loan", loan.Id, $"{Py.Title(loan.RequestType)} #{loan.Id} - {me.FullName}", ct);
        if (MyManagerId is int mgr && mgr != 0)
            await notify.ToAsync(mgr, "Loan Request", $"{me.FullName} requests a loan of Rs.{Py.Comma0(amount)}",
                                 "info", "loans", loan.Id, Me, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Loan request submitted.", "success");
        return Redirect("/ess/");
    }

    [HttpGet("slips")]
    [AnyMember]
    public async Task<IActionResult> Slips(CancellationToken ct)
    {
        ViewData["BackUrl"] = "/ess/";
        return View(await SlipsAsync(null, ct));
    }

    public sealed class PerformanceModel
    {
        public IReadOnlyList<PerformanceReview> Reviews { get; init; } = [];
        public IReadOnlyList<PerformanceGoal> Goals { get; init; } = [];
    }

    [HttpGet("performance")]
    [AnyMember]
    public async Task<IActionResult> Performance(CancellationToken ct)
    {
        ViewData["BackUrl"] = "/ess/";
        return View(new PerformanceModel
        {
            Reviews = await db.Set<PerformanceReview>().AsNoTracking().Where(r => r.UserId == Me).OrderByDescending(r => r.CreatedAt).ToListAsync(ct),
            Goals = await db.Set<PerformanceGoal>().AsNoTracking().Where(g => g.UserId == Me).OrderByDescending(g => g.CreatedAt).ToListAsync(ct),
        });
    }
}

/// <summary>ess.avatar: open to anyone signed in (the shell shows it on every page; module_guard ALWAYS_OPEN).</summary>
[Area("Hr")]
[SignedIn]
public sealed class AvatarController(FileStore files) : Controller
{
    [HttpGet("/ess/avatar/{filename}")]
    public async Task<IActionResult> Avatar(string filename, CancellationToken ct)
    {
        var stored = await files.ReadAsync($"avatars/{FileStore.SecureFilename(filename)}", ct: ct);
        if (stored is null) return new ContentResult { Content = "", StatusCode = 404 };
        var (data, type) = stored.Value;
        return File(data, string.IsNullOrEmpty(type) ? "image/png" : type);
    }
}
