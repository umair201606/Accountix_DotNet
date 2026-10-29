using Accountix.HrRecords.Services;

namespace Accountix.HrRecords.Controllers;

/// <summary>What every controller of this project shares: the person, Python's answers and refusals.</summary>
public abstract class RecordsController : Controller
{
    protected EffectiveAccess Access => HttpContext.Access()!;
    protected int Me => Access.User.Id;
    protected int Cid => Access.CompanyId!.Value;
    /// <summary>current_user.is_admin(): admin here, or a super admin.</summary>
    protected bool IsAdmin => Access.IsAdmin;
    /// <summary>current_user.is_manager(): manager here, or a super admin.</summary>
    protected bool IsManager => Access.IsManager;
    protected bool IsLead => Access.IsAdmin || Access.IsManager;

    /// <summary>jsonify(value), status.</summary>
    protected static IActionResult PyJson(object? value, int status = 200) => new PyJsonResult(value, status);

    /// <summary>jsonify({"error": message}), status.</summary>
    protected static IActionResult PyError(string message, int status) =>
        new PyJsonResult(new Dictionary<string, object?> { ["error"] = message }, status);

    protected static IActionResult PySuccess() => new PyJsonResult(new Dictionary<string, object?> { ["success"] = true });

    /// <summary>abort(404): the app's "Not Found" page.</summary>
    protected IActionResult NotFoundPage() => new ContentResult
    {
        StatusCode = 404,
        ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(404, "Not Found", "The page or record you're looking for doesn't exist."),
    };

    /// <summary>flash(message, category) + redirect(to).</summary>
    protected IActionResult FlashTo(string message, string category, string to)
    {
        this.Flash(message, category);
        return Redirect(to);
    }

    /// <summary>request.form.get(name, default).</summary>
    protected string FormStr(string name, string @default = "") =>
        Request.HasFormContentType && Request.Form.TryGetValue(name, out var v) && v.Count > 0 ? v[0] ?? @default : @default;

    /// <summary>request.form.get(name) (null when absent).</summary>
    protected string? FormOpt(string name) =>
        Request.HasFormContentType && Request.Form.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;

    /// <summary>request.form.get(name, type=int): null when missing or not a whole number.</summary>
    protected int? FormInt(string name) => Py.Int(FormOpt(name));

    /// <summary>request.args.get(name, default, type=int).</summary>
    protected int QueryInt(string name, int @default) => Py.Int(Request.Query[name].FirstOrDefault()) ?? @default;
}
