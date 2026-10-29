using System.Globalization;

namespace Accountix.Platform.Controllers;

/// <summary>Jinja-isms the ported settings templates use, so the markup reads like the original.</summary>
public static class J
{
    /// <summary>{{ v or 'd' }}: Python's falsy (None, "") falls back.</summary>
    public static string Or(string? v, string d = "") => string.IsNullOrEmpty(v) ? d : v;

    public static string Or(int? v, string d = "") => v is int i && i != 0 ? i.ToString(CultureInfo.InvariantCulture) : d;

    /// <summary>{{ 'selected' if cond }}.</summary>
    public static string Sel(bool cond) => cond ? "selected" : "";

    public static string Chk(bool cond) => cond ? "checked" : "";

    /// <summary>{{ x }} of a Python value: None prints "None", True "True", floats as repr.</summary>
    public static string Py(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        decimal m => Audit.PyJson.FloatRepr((double)m),
        double d => Audit.PyJson.FloatRepr(d),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>strftime('%d %b %Y').</summary>
    public static string Dmy(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public static string Dmy(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
}
