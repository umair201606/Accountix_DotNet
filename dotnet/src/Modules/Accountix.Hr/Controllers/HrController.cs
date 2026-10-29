using System.Net;

namespace Accountix.Hr.Controllers;

/// <summary>What every HR controller shares: the person, Python's answers and refusals.</summary>
public abstract class HrController : Controller
{
    protected EffectiveAccess Access => HttpContext.Access()!;
    protected int Me => Access.User.Id;
    protected int Cid => Access.CompanyId!.Value;
    protected bool IsAdmin => Access.IsAdmin;
    protected bool IsManager => Access.IsManager;
    protected bool IsLead => Access.IsAdmin || Access.IsManager;

    /// <summary>The signed-in person's department / designation / manager in this company (membership, else the account copy).</summary>
    protected string? MyDepartment => Access.Membership is { } m ? m.Department : Access.User.Department;
    protected int? MyManagerId => Access.Membership is { } m ? m.ManagerId : Access.User.ManagerId;

    /// <summary>flash("Access denied.", "danger") + redirect(url_for("dashboard")).</summary>
    protected IActionResult AccessDenied(string to = "/dashboard")
    {
        this.Flash("Access denied.", "danger");
        return Redirect(to);
    }

    /// <summary>jsonify(body), status.</summary>
    protected IActionResult JsonStatus(object body, int status) => new JsonResult(body) { StatusCode = status };

    /// <summary>request.is_json or request.accept_mimetypes.best == "application/json".</summary>
    protected bool AsksJson()
    {
        if (Request.HasJsonContentType()) return true;
        string accept = Request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept)) return false;
        string? best = null;
        double bestQ = -1;
        foreach (string part in accept.Split(','))
        {
            var bits = part.Split(';');
            string type = bits[0].Trim().ToLowerInvariant();
            double q = 1;
            foreach (string b in bits.Skip(1))
            {
                var kv = b.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim() == "q" && double.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    q = v;
            }
            if (q > bestQ) { bestQ = q; best = type; }
        }
        return best == "application/json";
    }

    /// <summary>request.remote_addr (the direct peer, as Flask without ProxyFix).</summary>
    protected string? RemoteAddr()
    {
        IPAddress? ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    /// <summary>request.form.get(name, type=int): null when missing or not a whole number.</summary>
    protected int? FormInt(string name) =>
        int.TryParse((Request.HasFormContentType ? Request.Form[name].FirstOrDefault() : null)?.Trim(), out int v) ? v : null;

    protected string FormStr(string name, string @default = "") =>
        Request.HasFormContentType ? Request.Form[name].FirstOrDefault() ?? @default : @default;

    /// <summary>request.args.get(name, default, type=int).</summary>
    protected int QueryInt(string name, int @default) =>
        int.TryParse(Request.Query[name].FirstOrDefault(), out int v) ? v : @default;

    /// <summary>request.form.get(name, type=float): null when missing or unreadable.</summary>
    protected double? FormFloat(string name)
    {
        string? raw = Request.HasFormContentType ? Request.Form[name].FirstOrDefault() : null;
        return double.TryParse(raw?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
               && !double.IsNaN(v) ? v : null;
    }
}
