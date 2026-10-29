using Accountix.Platform.Audit;
using Accountix.Platform.Services;
using Accountix.SuperAdmin.Infrastructure;

namespace Accountix.SuperAdmin.Controllers;

/// <summary>
/// superadmin.login: a separate door to the platform console. Same
/// credentials as /auth/login — there is one user table — but it refuses
/// anyone without the super admin flag and lands straight on the console.
/// A tenant who signs in correctly here is told the same thing as a wrong
/// password: "your password is right, you are just not a super admin" would
/// make this page a super-admin oracle. Same throttle as every sign-in, and a
/// server-side session row (user_sessions).
/// </summary>
[Area("SuperAdmin")]
[Route("superadmin")]
public sealed class LoginController(SignInService signIn, AccountSecurity security, AuditTrail audit) : Controller
{
    public const string Refused = "Those credentials do not open the super admin console.";

    [HttpGet("login")]
    public IActionResult Login()
    {
        if (HttpContext.Access()?.IsSuperAdmin == true)
            return Redirect("/superadmin/");
        return View("Login");
    }

    [HttpPost("login")]
    public async Task<IActionResult> LoginPost(CancellationToken ct)
    {
        if (HttpContext.Access()?.IsSuperAdmin == true)
            return Redirect("/superadmin/");
        var form = Request.Form;
        string loginId = Py.Strip(Py.Form(form, "login"));
        string password = Py.Form(form, "password");
        string shown = loginId.Length > 80 ? loginId[..80] : loginId;
        var user = await signIn.FindLoginAsync(loginId, ct);
        if (await security.ThrottledAsync(loginId, user, ct))
        {
            await security.RecordFailedSignInAsync($"Console sign-in throttled for '{shown}'", loginId, user, null, ct);
            this.Flash(AccountSecurity.ThrottledMessage, "error");
            var page = View("Login");
            page.StatusCode = StatusCodes.Status429TooManyRequests;
            return page;
        }
        if (user is null)
            AccountSecurity.SpendPasswordCheck(password);
        if (user is not null && SignInService.CheckPassword(user, password) && user.IsActive == true && user.IsSuperAdmin == true)
        {
            // session.clear(); login_user(user); sessions.start(user)
            await signIn.StartAsync(HttpContext, user, ct);
            string name = string.IsNullOrEmpty(user.FullName) ? user.Email : user.FullName;
            await audit.RecordAsync("login", $"Super admin {name} entered the console", companyId: null,
                                    userId: user.Id, userName: name, ct: ct);
            this.Flash($"Welcome back, {name}.", "success");
            string? next = SafeUrl.Local(Request.Query["next"].FirstOrDefault());
            if (next is not null && next.StartsWith("/superadmin", StringComparison.Ordinal))
                return Redirect(next);
            return Redirect("/superadmin/");
        }
        await security.RecordFailedSignInAsync($"Failed console sign-in for '{shown}'", loginId, user, null, ct);
        this.Flash(Refused, "error");
        return View("Login");
    }
}
