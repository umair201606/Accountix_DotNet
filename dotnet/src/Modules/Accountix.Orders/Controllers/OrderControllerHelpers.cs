using Accountix.Invoicing.Services;
using Accountix.Orders.Services;

namespace Accountix.Orders.Controllers;

/// <summary>Answers both order controllers give the way Flask does.</summary>
internal static class OrderAnswers
{
    /// <summary>handle_all: the 500 page (an exception the Python route did not catch).</summary>
    public static ContentResult ServerError() => new()
    {
        StatusCode = 500, ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(500, "Server error", "Something went wrong on our side. The error has been logged."),
    };

    /// <summary>request.get_json(force=True) on a body that is not JSON.</summary>
    public static ContentResult BadRequest() => new()
    {
        StatusCode = 400, ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(400, "Bad Request", "Failed to decode JSON object."),
    };

    /// <summary>abort(404) from scoped_get_404.</summary>
    public static ContentResult NotFound() => new()
    {
        StatusCode = 404, ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(404, "Not Found", "The page or record you're looking for doesn't exist."),
    };

    public static IActionResult From(OrderAnswer? answer) => answer is null ? NotFound() : answer.ToResult();

    /// <summary>
    /// rights_guard _json_save: an existing order needs edit (approve when
    /// approving); a new one create (and approve when saved straight to approved).
    /// The guard reads the body leniently: anything but a JSON object counts as {}.
    /// </summary>
    public static IActionResult? DenySave(ControllerBase c, string section, PyData? data)
    {
        bool isObject = data?.IsObject == true;
        bool approve = isObject && OrderInput.IsApprove(data!);
        bool existing = isObject && PyData.Truthy(data!.Get("id"));
        if (existing)
            return c.Deny(section, approve ? Rights.Approve : Rights.Edit, json: true);
        return c.Deny(section, Rights.Create, json: true)
               ?? (approve ? c.Deny(section, Rights.Approve, json: true) : null);
    }

    /// <summary>request.args.get("q", "").strip().</summary>
    public static string Q(HttpRequest request) => (request.Query["q"].FirstOrDefault() ?? "").Trim();
}
