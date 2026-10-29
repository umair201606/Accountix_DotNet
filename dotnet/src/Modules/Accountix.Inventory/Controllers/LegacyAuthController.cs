namespace Accountix.Inventory.Controllers;

/// <summary>
/// The inventory app's own sign-in door and sign-out (legacy URLs, OPEN in
/// rights_guard): both hand over to the one sign-in.
/// </summary>
[Route("inventory")]
public sealed class LegacyAuthController : Controller
{
    [HttpGet("login")]
    [HttpPost("login")]
    [IgnoreAntiforgeryToken]
    public IActionResult Login() => Redirect("/auth/login");

    /// <summary>login_required, then auth.logout (which records it and clears the session).</summary>
    [HttpGet("logout")]
    public IActionResult Logout() => HttpContext.Access() is null ? Refusal.SignIn() : Redirect("/auth/logout");
}
