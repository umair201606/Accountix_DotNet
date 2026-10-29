using Accountix.HrRecords.Services;

namespace Accountix.HrRecords.Controllers;

/// <summary>
/// hr_app/routes/digital_file.py (blueprint "digital_files"): each person's
/// documents, kept in stored_files under "&lt;user_id&gt;/&lt;file&gt;" (files from
/// before the database store are still read from the upload folder). People
/// open and delete their own; admins open, verify and review everyone's.
/// </summary>
[Area("HrRecords")]
[Module(Modules.Hr)]
[Route("digital-files")]
public sealed class DigitalFilesController(AccountixDbContext db, FileStore store, CompanyClock clock) : RecordsController
{
    private static readonly HashSet<string> Allowed = ["pdf", "png", "jpg", "jpeg"];

    public sealed record FileRow(int Id, string Employee, string Title, string? Category, string OriginalName, int? FileSize,
                                 DateOnly? ExpiryDate, bool IsVerified, DateTime? UploadedAt);

    public sealed record IndexModel(IReadOnlyList<FileRow> Files, IReadOnlyList<(int Id, string Name)> Categories, int Expiring, DateOnly Now);

    public sealed record AdminModel(IReadOnlyList<FileRow> Files, int Expired, int Expiring, DateOnly Now);

    public sealed record ProfileModel(User Emp, string? Designation, string? Department, string? ManagerName,
                                      IReadOnlyList<FileRow> Files, int AttendanceCount, int TimesheetCount, int SlipCount, int ReviewCount);

    /// <summary>The storage key of a file's bytes inside its company: the upload folder's old per-person layout.</summary>
    public static string FileKey(DigitalFile f) => $"{f.UserId}/{f.Filename}";

    private IQueryable<FileRow> Rows(IQueryable<DigitalFile> q) =>
        q.Select(f => new FileRow(f.Id, f.User.FullName, f.Title, f.Category != null ? f.Category.Name : null, f.OriginalName,
                                  f.FileSize, f.ExpiryDate, f.IsVerified == true, f.UploadedAt));

    [HttpGet("")]
    [AnyMember]   // everyone's own documents
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var files = await Rows(db.Set<DigitalFile>().AsNoTracking().Where(f => f.UserId == Me)
            .OrderByDescending(f => f.UploadedAt).ThenByDescending(f => f.Id)).ToListAsync(ct);
        var categories = await db.Set<FileCategory>().AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var now = clock.Today;
        int expiring = files.Count(f => f.ExpiryDate is DateOnly e && e <= now.AddDays(30) && e > now);
        return View(new IndexModel(files, categories.Select(c => (c.Id, c.Name)).ToList(), expiring, now));
    }

    [HttpPost("upload")]
    [AnyMember]   // files into the uploader's own record only
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var file = Request.HasFormContentType ? Request.Form.Files["file"] : null;
        if (file is null || file.FileName == "")
            return FlashTo("No file selected.", "danger", "/digital-files/");
        string name = file.FileName;
        string ext = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant() : "";
        if (!Allowed.Contains(ext))
            return FlashTo("Only PDF, PNG, JPG files allowed.", "danger", "/digital-files/");
        // A category id from another company (or none at all) was stored as typed.
        int? categoryId = FormInt("category_id");
        if (categoryId is int c && !await db.Set<FileCategory>().AnyAsync(x => x.Id == c, ct))
            categoryId = null;
        if (categoryId == 0) categoryId = null;
        DateOnly? expiry = null;
        string? expiryText = FormOpt("expiry_date");
        if (!string.IsNullOrEmpty(expiryText))
        {
            if (!Py.TryParseDate(expiryText, out var e))
                return FlashTo("Enter the expiry date as YYYY-MM-DD.", "danger", "/digital-files/");
            expiry = e;
        }
        byte[] data;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }
        string? title = FormOpt("title");
        var df = new DigitalFile
        {
            UserId = Me,
            CategoryId = categoryId,
            Title = string.IsNullOrEmpty(title) ? name : title,
            Filename = $"{Guid.NewGuid():N}.{ext}",
            OriginalName = name,
            FileSize = data.Length,
            MimeType = string.IsNullOrEmpty(file.ContentType) ? null : file.ContentType,
            Notes = FormStr("notes"),
            ExpiryDate = expiry,
            IsVerified = false,
            UploadedAt = clock.UtcNow,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Add(df);
        await db.SaveChangesAsync(ct);
        await store.SaveAsync(FileKey(df), data, df.MimeType, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return FlashTo("File uploaded.", "success", "/digital-files/");
    }

    [HttpGet("download/{fid:int}")]
    [AnyMember]   // the owner or an admin, checked in the route
    public async Task<IActionResult> Download(int fid, CancellationToken ct)
    {
        var f = await db.Set<DigitalFile>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == fid, ct);
        if (f is null) return NotFoundPage();
        if (f.UserId != Me && !IsAdmin)
            return FlashTo("Access denied.", "danger", "/digital-files/");
        var stored = await store.ReadAsync(FileKey(f), f.CompanyId, ct);
        if (stored is null) return NotFoundPage();
        var (data, contentType) = stored.Value;
        return File(data, string.IsNullOrEmpty(f.MimeType) ? contentType : f.MimeType, f.OriginalName);
    }

    [HttpPost("delete/{fid:int}")]
    [AnyMember]   // the owner or an admin, checked in the route
    public async Task<IActionResult> Delete(int fid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Locked, so two deletes at once remove it once (the second finds nothing).
        var f = await db.LockAsync<DigitalFile>(fid, ct);
        if (f is null) return NotFoundPage();
        if (f.UserId != Me && !IsAdmin)
            return FlashTo("You can delete only your own documents.", "danger", "/digital-files/");
        await store.DeleteAsync(FileKey(f), f.CompanyId, ct);
        db.Remove(f);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return FlashTo("File deleted.", "success", "/digital-files/");
    }

    [HttpGet("admin")]
    [AnyMember]   // admins only, checked in the route
    public async Task<IActionResult> Admin(CancellationToken ct)
    {
        if (!IsAdmin)
            return FlashTo("Access denied.", "danger", "/dashboard");
        var files = await Rows(db.Set<DigitalFile>().AsNoTracking()
            .OrderByDescending(f => f.UploadedAt).ThenByDescending(f => f.Id)).ToListAsync(ct);
        var now = clock.Today;
        int expired = files.Count(f => f.ExpiryDate is DateOnly e && e < now);
        int expiring = files.Count(f => f.ExpiryDate is DateOnly e && e <= now.AddDays(30) && e >= now);
        return View(new AdminModel(files, expired, expiring, now));
    }

    [HttpPost("verify/{fid:int}")]
    [AnyMember]   // admins only, checked in the route
    public async Task<IActionResult> Verify(int fid, CancellationToken ct)
    {
        if (!IsAdmin)
            return PyError("Access denied", 403);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var f = await db.LockAsync<DigitalFile>(fid, ct);
        if (f is null) return NotFoundPage();
        f.IsVerified = true;
        f.VerifiedBy = Me;
        f.VerifiedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PySuccess();
    }

    [HttpGet("check-expiry")]
    [AnyMember]   // admins only, checked in the route
    public async Task<IActionResult> CheckExpiry(CancellationToken ct)
    {
        if (!IsAdmin)
            return PyError("Access denied", 403);
        var now = clock.Today;
        var warning = now.AddDays(30);
        var rows = await db.Set<DigitalFile>().AsNoTracking()
            .Where(f => f.ExpiryDate != null && f.ExpiryDate <= warning)
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.Title, User = f.User.FullName, f.ExpiryDate })
            .ToListAsync(ct);
        Dictionary<string, object?> Item(int id, string title, string user, DateOnly? expiry) => new()
        {
            ["id"] = id, ["title"] = title, ["user"] = user, ["expiry"] = Py.Date(expiry),
        };
        var expiring = rows.Where(r => r.ExpiryDate >= now).Select(r => Item(r.Id, r.Title, r.User, r.ExpiryDate)).ToList();
        var expired = rows.Where(r => r.ExpiryDate < now).Select(r => Item(r.Id, r.Title, r.User, r.ExpiryDate)).ToList();
        return PyJson(new Dictionary<string, object?>
        {
            ["expiring_count"] = expiring.Count,
            ["expired_count"] = expired.Count,
            ["expiring"] = expiring,
            ["expired"] = expired,
        });
    }

    [HttpGet("profile/{uid:int}")]
    [AnyMember]   // an admin, or the person themself
    public async Task<IActionResult> Profile(int uid, CancellationToken ct)
    {
        if (!IsAdmin && Me != uid)
            return FlashTo("Access denied.", "danger", "/dashboard");
        var emp = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (emp is null) return NotFoundPage();
        bool self = emp.IsSuperAdmin == true && emp.Id == Me;
        var membership = await db.Memberships.AsNoTracking().Where(m => m.CompanyId == Cid && m.UserId == uid)
            .OrderBy(m => m.Id).Select(m => new { m.Status, m.ManagerId, m.Designation, m.Department }).ToListAsync(ct);
        if (!self && !membership.Any(m => m.Status == CompanyMembership.StatusActive))
            return NotFoundPage();
        // User.manager_id / designation / department read this company's membership, else the account copy.
        var emp0 = membership.FirstOrDefault();
        int? managerId = emp0 is null ? emp.ManagerId : emp0.ManagerId;
        string? designation = emp0 is null ? emp.Designation : emp0.Designation;
        string? department = emp0 is null ? emp.Department : emp0.Department;
        string? managerName = managerId is int mid
            ? await db.Users.AsNoTracking().Where(u => u.Id == mid).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;
        var files = await Rows(db.Set<DigitalFile>().AsNoTracking().Where(f => f.UserId == uid)
            .OrderByDescending(f => f.UploadedAt).ThenByDescending(f => f.Id)).ToListAsync(ct);
        int attendance = await db.Set<Attendance>().CountAsync(a => a.UserId == uid, ct);
        int timesheets = await db.Set<TimesheetWeek>().CountAsync(t => t.UserId == uid && t.Status == "approved", ct);
        int slips = await db.Set<PayrollSlip>().CountAsync(s => s.UserId == uid, ct);
        int reviews = await db.Set<PerformanceReview>().CountAsync(r => r.UserId == uid, ct);
        return View(new ProfileModel(emp, designation, department, managerName, files, attendance, timesheets, slips, reviews));
    }
}
