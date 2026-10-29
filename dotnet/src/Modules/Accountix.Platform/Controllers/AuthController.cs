using Accountix.Core.Security;
using Accountix.Platform.Audit;
using Accountix.Platform.Services;

namespace Accountix.Platform.Controllers;

/// <summary>
/// The public pages and the sign-in door: app.py landing / signup / admin
/// entry / company switch, hr_app/routes/auth.py login and logout.
/// </summary>
[Area("Platform")]
public sealed class AuthController(AccountixDbContext db, SignInService signIn, AccountSecurity security, AuditTrail audit,
                                   TimeProvider time) : Controller
{
    private bool SignedIn => HttpContext.Access() is not null;

    // ── Landing ───────────────────────────────────────────────────────────

    /// <summary>Public landing page; signed-in people go where sign-in would have sent them (the portal).</summary>
    [HttpGet("/")]
    public IActionResult Landing() => SignedIn ? Redirect("/portal/") : View("Landing");

    // ── Sign in / out ─────────────────────────────────────────────────────

    [HttpGet("/auth/login")]
    public IActionResult Login() => SignedIn ? Redirect("/portal/") : View("Login");

    [HttpPost("/auth/login")]
    public async Task<IActionResult> LoginPost(CancellationToken ct)
    {
        if (SignedIn) return Redirect("/portal/");
        // Users sign in with their User ID; the email still works (login_id defaults to it).
        string loginId = ((string?)Request.Form["login"] is { Length: > 0 } l ? l : (string?)Request.Form["email"] ?? "").Trim();
        string password = Request.Form["password"].ToString();
        var user = await signIn.FindLoginAsync(loginId, ct);
        string shown = loginId.Length > 80 ? loginId[..80] : loginId;
        if (await security.ThrottledAsync(loginId, user, ct))
        {
            // Refused before the password is looked at; the attempt still counts.
            await security.RecordFailedSignInAsync($"Sign-in throttled for '{shown}'", loginId, user, ct: ct);
            this.Flash(AccountSecurity.ThrottledMessage, "danger");
            var r = View("Login");
            r.StatusCode = 429;
            return r;
        }
        if (user is null)
            AccountSecurity.SpendPasswordCheck(password);
        if (user is not null && SignInService.CheckPassword(user, password))
        {
            // Super admins have their own door; refused exactly like a bad login.
            if (user.SuperAdmin)
            {
                await security.RecordFailedSignInAsync($"Sign-in refused for {loginId} (super admin must use the console)", loginId, user, ct: ct);
                this.Flash("Invalid email or password.", "danger");
                return View("Login");
            }
            if (!user.Active)
            {
                this.Flash("Your account has been deactivated.", "danger");
                return View("Login");
            }
            // A fresh session: nothing set before sign-in carries over.
            await signIn.StartAsync(HttpContext, user, ct);
            string name = string.IsNullOrEmpty(user.FullName) ? user.Email : user.FullName;
            await audit.RecordAsync("login", $"{name} signed in", companyId: null, userId: user.Id, userName: name, ct: ct);
            this.Flash($"Welcome back, {name}.", "success");
            return Redirect(SafeUrl.Local(Request.Query["next"]) ?? "/portal/");
        }
        await security.RecordFailedSignInAsync($"Failed sign-in for '{shown}'", loginId, user, ct: ct);
        this.Flash("Invalid email or password.", "danger");
        return View("Login");
    }

    [HttpGet("/auth/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var access = HttpContext.Access();
        if (access is null) return Refusal.SignIn();
        // A link on another site (<img src=".../logout">) must not sign anyone out.
        if (Request.Headers["Sec-Fetch-Site"] == "cross-site")
            return Redirect("/portal/");
        string name = string.IsNullOrEmpty(access.User.FullName) ? access.User.Email : access.User.FullName;
        await audit.RecordNowAsync("logout", $"{name} signed out", ct: ct);
        await signIn.EndCurrentAsync(HttpContext, ct: ct);
        signIn.Clear(HttpContext);
        this.Flash("You have been logged out.", "info");
        return Redirect("/auth/login");
    }

    // ── Sign-up (request platform access) ─────────────────────────────────

    public sealed record SignupModel(bool Submitted, IFormCollection? Form)
    {
        public string Get(string key) => Form?[key].ToString() ?? "";
    }

    [HttpGet("/signup")]
    public IActionResult Signup() => View("Signup", new SignupModel(false, null));

    [HttpPost("/signup")]
    public async Task<IActionResult> SignupPost(CancellationToken ct)
    {
        var f = Request.Form;
        string fullName = f["full_name"].ToString().Trim();
        string email = f["email"].ToString().Trim().ToLowerInvariant();
        string password = f["password"].ToString();
        string phone = f["phone"].ToString().Trim();
        string companyName = f["company_name"].ToString().Trim();
        string notes = f["notes"].ToString().Trim();
        IActionResult Again(string message)
        {
            this.Flash(message, "error");
            return View("Signup", new SignupModel(false, f));
        }
        if (fullName.Length == 0 || email.Length == 0 || password.Length == 0)
            return Again("Name, email and password are required.");
        if (AccountSecurity.PasswordProblem(password) is { } problem)
            return Again(problem);
        if (!email.Contains('@') || email.Any(char.IsWhiteSpace))
            return Again("Enter a valid email address.");
        string? phoneOrNull = phone.Length == 0 ? null : phone;
        // A blocked request stays blocked: a fresh one with the same email or phone does not re-queue it.
        if (await db.Set<RegistrationRequest>().AnyAsync(r => r.Status == "blocked"
                && (r.Email == email || (r.Phone != null && r.Phone == phoneOrNull)), ct))
            return Again("A request with these details has already been reviewed. Contact the platform administrator.");
        if (await db.Users.AnyAsync(u => u.Email == email || u.LoginId!.ToLower() == email, ct))
            return Again("An account with this email already exists. Sign in or use a different email.");
        if (await AccountSecurity.RegistrationEmailExistsAsync(db, email, ct))
            return Again("A request with this email is already pending review.");
        if (phone.Length > 0 && await db.Users.AnyAsync(u => u.Phone == phone, ct))
            return Again("This phone number is already registered.");
        if (phone.Length > 0 && await AccountSecurity.RegistrationPhoneExistsAsync(db, phone, ct))
            return Again("This phone number already has a pending request.");
        db.Add(new RegistrationRequest
        {
            FullName = fullName, Email = email, Phone = phoneOrNull,
            CompanyName = companyName.Length == 0 ? null : companyName, Notes = notes.Length == 0 ? null : notes,
            PasswordHash = PasswordHasher.Hash(password), Status = "pending", Seen = false,
            CreatedAt = FirstUse.Micro(time.GetUtcNow().UtcDateTime),
        });
        await db.SaveChangesAsync(ct);
        return View("Signup", new SignupModel(true, null));
    }

    [HttpGet("/signup/check-email")]
    public async Task<IActionResult> CheckEmail(string? email, CancellationToken ct)
    {
        string e = (email ?? "").Trim().ToLowerInvariant();
        bool exists = await db.Users.AnyAsync(u => u.Email == e, ct) || await AccountSecurity.RegistrationEmailExistsAsync(db, e, ct);
        return Json(new { exists });
    }

    [HttpGet("/signup/check-phone")]
    public async Task<IActionResult> CheckPhone(string? phone, CancellationToken ct)
    {
        string p = (phone ?? "").Trim();
        bool exists = await db.Users.AnyAsync(u => u.Phone == p, ct) || await AccountSecurity.RegistrationPhoneExistsAsync(db, p, ct);
        return Json(new { exists });
    }

    // ── Private operator entry, company switch ────────────────────────────

    /// <summary>Not linked from anywhere public: the console's door (or the console when already a super admin there).</summary>
    [HttpGet("/admin")]     // also answers /admin/
    public IActionResult AdminEntry() =>
        HttpContext.Access()?.IsSuperAdmin == true ? Redirect("/superadmin/") : Redirect("/superadmin/login");

    /// <summary>
    /// Company switcher: only an ACTIVE membership of an ACTIVE company; a
    /// navigation started by another website is refused.
    /// </summary>
    [HttpGet("/company/switch/{companyId:int}")]
    public async Task<IActionResult> CompanySwitch(int companyId, string? next, CancellationToken ct)
    {
        var access = HttpContext.Access();
        if (access is null) return Refusal.SignIn();
        if (Request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            this.Flash("Choose the company from inside the app.", "error");
            return Redirect("/dashboard/");
        }
        bool member = await db.Memberships.AnyAsync(m => m.CompanyId == companyId && m.UserId == access.User.Id
                                                         && m.Status == CompanyMembership.StatusActive, ct);
        if (!member)
        {
            this.Flash("You don't have access to that company.", "error");
            return Redirect("/dashboard/");
        }
        var target = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (target is null || !target.Active)
        {
            this.Flash("That company is suspended — its books cannot be opened.", "error");
            return Redirect("/portal/");
        }
        signIn.SetCompany(HttpContext, companyId);
        return Redirect(SafeUrl.Local(next) ?? "/dashboard/");
    }

    /// <summary>The app shell's "ERP Hub" link (/hub): the hub lives at /dashboard/.</summary>
    [HttpGet("/hub")]
    public IActionResult HubAlias() => Redirect("/dashboard/");
}
