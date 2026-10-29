using Microsoft.AspNetCore.Mvc.Filters;

namespace Accountix.Platform.Controllers;

/// <summary>Flask-Login's @login_required: signed-in people only (no company or module needed).</summary>
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
