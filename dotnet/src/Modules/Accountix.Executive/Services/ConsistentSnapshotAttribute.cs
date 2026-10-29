using System.Data;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accountix.Executive.Services;

/// <summary>
/// consistent_snapshot (shared/concurrency.py): the report runs in one
/// REPEATABLE READ transaction, so every query sees the books as of the same
/// instant — a posting landing between two of Books Integrity's queries can no
/// longer make it flag a mismatch the ledger does not have. Read-only: the
/// transaction is never committed (disposing it rolls back).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ConsistentSnapshotAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<AccountixDbContext>();
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is not null)
        {
            await next();
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, context.HttpContext.RequestAborted);
        await next();
    }
}
