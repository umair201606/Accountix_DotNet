using Accountix.SuperAdmin.Infrastructure;
using Accountix.SuperAdmin.Services;

namespace Accountix.SuperAdmin.Controllers;

/// <summary>
/// Access requests (superadmin.requests, request_action): visitors who asked
/// for platform access. Review, approve (creates the account) or block.
/// </summary>
[Area("SuperAdmin")]
[Route("superadmin")]
[SuperAdminOnly]
public sealed class RequestsController(AccountixDbContext db, ConsoleAccounts accounts) : Controller
{
    public const string Pending = "pending", Seen = "seen", Approved = "approved", BlockedStatus = "blocked";

    private User Me => HttpContext.Access()!.User;

    public sealed record ListModel(IReadOnlyList<RegistrationRequest> Requests, int PendingCount, int ApprovedCount, int BlockedCount);

    [HttpGet("requests")]
    public async Task<IActionResult> Requests(CancellationToken ct)
    {
        var all = await db.Set<RegistrationRequest>().AsNoTracking().OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        int pending = all.Count(r => r.Status is Pending or Seen);
        int approved = all.Count(r => r.Status == Approved);
        int blocked = all.Count(r => r.Status == BlockedStatus);
        // Python marks unseen pending requests as seen for this render, then
        // (its "anything still unseen?" test running after the loop) never
        // commits: the page shows them as seen and the database keeps them unseen.
        foreach (var r in all)
            if (r.Status == Pending && !r.Seen)
                r.Seen = true;
        ViewData["Nav"] = "requests";
        ViewData["PendingCount"] = pending;
        return View("Requests", new ListModel(all, pending, approved, blocked));
    }

    /// <summary>Approve, block, unblock or delete one request, locked so two clicks never make two accounts.</summary>
    [HttpPost("requests/{requestId:int}/{verb}")]
    public async Task<IActionResult> RequestAction(int requestId, string verb, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // with_for_update().populate_existing(): FOR UPDATE, as Python takes it.
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM registration_requests WHERE id = {0} FOR UPDATE", [requestId], ct);
        var req = await db.Set<RegistrationRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (req is null)
            return ConsolePages.Error(404);
        const string back = "/superadmin/requests/";
        var now = accounts.UtcNow;
        switch (verb)
        {
            case "approve":
            {
                if (req.Status == Approved)
                {
                    this.Flash("This request was already approved.", "info");
                    return Redirect(back);
                }
                string e = req.Email.ToLowerInvariant();
                if (await db.Users.AnyAsync(u => u.Email.ToLower() == e || u.LoginId!.ToLower() == e, ct))
                {
                    // Made by hand in Manage Users meanwhile, or the email became someone's User ID.
                    this.Flash($"An account already signs in as {req.Email} — the request was not turned into a second one.", "error");
                    return Redirect(back);
                }
                if (await accounts.RoleIdAsync(Role.Employee, ct) is not int employee)
                {
                    this.Flash("Roles are not seeded yet.", "error");
                    return Redirect(back);
                }
                await accounts.NewUserAsync(req.Email, req.FullName, employee, req.PasswordHash, allModules: false,
                                            phone: req.Phone, ct: ct);
                req.Status = Approved;
                req.Seen = true;
                req.ReviewedBy = Me.Id;
                req.ReviewedAt = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                this.Flash($"Access granted to {req.FullName} ({req.Email}). They can now sign in.", "success");
                break;
            }
            case "block":
                req.Status = BlockedStatus;
                req.Seen = true;
                req.ReviewedBy = Me.Id;
                req.ReviewedAt = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                this.Flash($"Request from {req.FullName} ({req.Email}) blocked.", "success");
                break;
            case "unblock":
                req.Status = Seen;
                req.ReviewedBy = Me.Id;
                req.ReviewedAt = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                this.Flash($"Request from {req.FullName} unblocked. Back to review queue.", "success");
                break;
            case "delete":
                db.Remove(req);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                this.Flash("Request deleted.", "success");
                break;
            default:
                this.Flash("Unknown action.", "error");
                break;
        }
        return Redirect(back);
    }
}
