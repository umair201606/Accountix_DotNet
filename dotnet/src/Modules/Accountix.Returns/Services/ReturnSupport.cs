using System.Globalization;
using System.Text.Json;
using Accountix.Invoicing.Services;

namespace Accountix.Returns.Services;

/// <summary>
/// What both return controllers share: Python-shaped JSON answers, the reading
/// of the JSON body the way the rights guard and the route read it, and the
/// error handlers app.py runs for these JSON endpoints.
/// </summary>
public static class ReturnSupport
{
    /// <summary>jsonify(...) with a status.</summary>
    public static ContentResult Answer(IDictionary<string, object?> payload, int status = 200) => PyJson.Result(payload, status);

    public static ContentResult Error(string message, int status, bool stale = false)
    {
        var d = new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };
        if (stale) d["stale"] = true;
        return PyJson.Result(d, status);
    }

    /// <summary>The body as text (read once).</summary>
    public static async Task<string> BodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>request.get_json(force=True, silent=True): the body when it is JSON, else null.</summary>
    public static JsonElement? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// rights_guard._json_save, then the route's own deny_json: a saved return
    /// needs approve (approving) or edit; a new one create, plus approve when it
    /// is saved straight to approved. JSON 403 in Python's words.
    /// </summary>
    public static IActionResult? DenySave(ControllerBase c, string section, JsonElement? root)
    {
        var guard = root is { ValueKind: JsonValueKind.Object } r ? new PyData(r) : new PyData(default);
        bool approve = guard.Str("action", null) == "approve";
        if (guard.Truthy("id"))
            return c.Deny(section, approve ? Rights.Approve : Rights.Edit, json: true);
        return c.Deny(section, Rights.Create, json: true)
               ?? (approve ? c.Deny(section, Rights.Approve, json: true) : null);
    }

    /// <summary>Flask's 400 page for a body get_json(force=True) cannot read.</summary>
    public static ContentResult BadRequestPage() => new()
    {
        StatusCode = 400,
        ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(400, "Bad Request",
            "The browser (or proxy) sent a request that this server could not understand."),
    };

    /// <summary>The Python class name app.py's handle_costing_refusal records ("refused" audit row).</summary>
    private static string? PythonName(BusinessRuleException e) => e switch
    {
        DocumentDateException => "DocumentDateError",
        FormInputException => "FormInputError",
        NegativeStockException => "NegativeStockError",
        ConsumedLayerException => "ConsumedLayerError",
        ClosedPeriodException => "ClosedPeriodError",
        _ => null,
    };

    /// <summary>
    /// Runs a JSON endpoint as app.py's handlers would: a costing / period / date
    /// refusal or a clash with another user's change rolls back (the body's
    /// transaction is disposed first) and answers 409 JSON {"ok": false, "error"} —
    /// these endpoints are fetch()-driven, whatever Accept header they send.
    /// </summary>
    public static async Task<IActionResult> GuardedAsync(HttpContext http, Func<Task<IActionResult>> body)
    {
        try
        {
            return await body();
        }
        catch (BusinessRuleException e)
        {
            if (PythonName(e) is string name)
            {
                try
                {
                    var audit = http.RequestServices.GetRequiredService<IAuditTrail>();
                    await audit.RecordNowAsync("refused", $"{name}: {e.Message}", module: "accounting");
                }
                catch (NotYetPortedException)
                {
                    // Until Platform registers the audit trail.
                }
            }
            return Error(e.Message, 409);
        }
        catch (Exception e) when (ConflictMessages.For(e) is string msg)
        {
            return Error(msg, 409);
        }
    }

    /// <summary>request.args.get(key, type=int): int(value), or None when absent or not an integer.</summary>
    public static int? ArgInt(string? value) =>
        value is not null && int.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                                          CultureInfo.InvariantCulture, out int i) ? i : null;

    /// <summary>float(Decimal): the float Python reads back.</summary>
    public static double F(decimal? d) => Py.F(d);

    /// <summary>"%d %b %Y" (e.g. "05 Oct 2026").</summary>
    public static string DayMonYear(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// A JSON value written to a Boolean column (SQLAlchemy's strict bool): true/false,
    /// None, or the ints 0 / 1; anything else is the TypeError Python raises.
    /// </summary>
    public static bool? Bool(JsonElement? e, bool @default)
    {
        if (e is not { } v) return @default;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number when v.GetRawText() is "0" or "1" => v.GetRawText() == "1",
            _ => throw new PyValueException($"Not a boolean value: {v.GetRawText()}"),
        };
    }

    /// <summary>str(value) of a JSON value as parse_doc_date reads it (None / "" fall back).</summary>
    public static string? DateText(JsonElement? e) => e switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.True } => "True",
        { ValueKind: JsonValueKind.False } => "False",
        { } v => v.GetRawText(),
    };
}
