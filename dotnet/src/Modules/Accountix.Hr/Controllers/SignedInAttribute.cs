using Microsoft.AspNetCore.Mvc.Filters;

namespace Accountix.Hr.Controllers;

/// <summary>
/// @login_required alone: signed in, no module gate. For the pages
/// shared/module_guard.py keeps ALWAYS_OPEN (profile, account settings,
/// change password, the avatar): a company that drops HR must not lock its
/// people out of their own account pages.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SignedInAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        if (ctx.HttpContext.Access() is null)
            ctx.Result = Refusal.SignIn();
        return Task.CompletedTask;
    }
}
