using System.Globalization;

namespace Accountix.Hr.Services;

/// <summary>
/// How Jinja prints Python values, so the pages read exactly as the Python
/// app's: floats as repr ("7.0", "2.5"), None as "None", '%g' formatting,
/// str.title(), strftime patterns.
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>repr(float): "7.0", "2.5", "0.1"; null prints "None".</summary>
    public static string Float(double? v)
    {
        if (v is null) return "None";
        double d = v.Value;
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        string s = d.ToString("R", Inv);
        if (s.Contains('E'))
        {
            // 1E-05 → 1e-05, 1E+16 → 1e+16
            var parts = s.Split('E');
            string exp = parts[1];
            string sign = exp.StartsWith('-') ? "-" : "+";
            string digits = exp.TrimStart('+', '-').PadLeft(2, '0');
            return parts[0] + "e" + sign + digits;
        }
        return s.Contains('.') ? s : s + ".0";
    }

    public static string Float(decimal? v) => Float(v is null ? null : (double)v.Value);

    /// <summary>'%g' % x (six significant digits, no trailing zeros).</summary>
    public static string G(double? v)
    {
        double d = v ?? 0;
        if (d == 0) return "0";
        string s = d.ToString("G6", Inv);
        if (s.Contains('E'))
        {
            var parts = s.Split('E');
            string exp = parts[1];
            string sign = exp.StartsWith('-') ? "-" : "+";
            return parts[0] + "e" + sign + exp.TrimStart('+', '-').PadLeft(2, '0');
        }
        return s;
    }

    public static string G(decimal? v) => G(v is null ? null : (double)v.Value);

    /// <summary>Jinja's round(value, n) on a float (returns a float, printed as repr).</summary>
    public static string Round(double v, int digits) => Float(Math.Round(v, digits, MidpointRounding.ToEven));

    /// <summary>Python's round(x, n) on a float.</summary>
    public static double R(double v, int digits) => Math.Round(v, digits, MidpointRounding.ToEven);

    /// <summary>str(x) for an int/None.</summary>
    public static string Str(int? v) => v?.ToString(Inv) ?? "None";

    public static string Str(bool? v) => v is null ? "None" : v.Value ? "True" : "False";

    public static string Str(string? v) => v ?? "None";

    /// <summary>A date as str(date): 2026-10-29.</summary>
    public static string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", Inv) ?? "None";

    /// <summary>str(datetime): '2026-10-29 09:30:00' (microseconds when present).</summary>
    public static string DateTime(DateTime? d)
    {
        if (d is null) return "None";
        var v = d.Value;
        string s = v.ToString("yyyy-MM-dd HH:mm:ss", Inv);
        long micro = (v.Ticks % TimeSpan.TicksPerSecond) / 10;
        return micro == 0 ? s : s + "." + micro.ToString("D6", Inv);
    }

    /// <summary>datetime.isoformat(): '2026-10-29T09:30:00' (microseconds when present).</summary>
    public static string Iso(DateTime? d) => d is null ? "" : DateTime(d).Replace(' ', 'T');

    /// <summary>strftime('%H:%M').</summary>
    public static string Hm(DateTime? d) => d?.ToString("HH:mm", Inv) ?? "";

    public static string Hms(DateTime? d) => d?.ToString("HH:mm:ss", Inv) ?? "";

    public static string Hm(TimeOnly? t) => t?.ToString("HH:mm", Inv) ?? "";

    /// <summary>strftime('%d-%b-%Y').</summary>
    public static string DMonY(DateTime? d) => d?.ToString("dd-MMM-yyyy", Inv) ?? "";

    public static string DMonY(DateOnly? d) => d?.ToString("dd-MMM-yyyy", Inv) ?? "";

    /// <summary>str.title(): every word's first letter upper, the rest lower (letters after any non-letter start a word).</summary>
    public static string Title(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = s.ToCharArray();
        bool prevLetter = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (char.IsLetter(c))
            {
                chars[i] = prevLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
                prevLetter = true;
            }
            else
                prevLetter = false;
        }
        return new string(chars);
    }

    /// <summary>
    /// The hr_text filter: an employment / contact field as the HR pages show it, empty for null and
    /// for the text "None" (the user editor used to print None into empty fields and save it back).
    /// </summary>
    public static string Text(string? v) => v is null or "None" ? "" : v;

    /// <summary>Past tense of a decision for the flash: approved, else rejected.</summary>
    public static string Decided(string? action) => action == "approve" ? "approved" : "rejected";

    /// <summary>s[:n].</summary>
    public static string Cut(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n];

    /// <summary>"{:,.0f}".format(x) (Python rounds half to even on the binary float).</summary>
    public static string Comma0(decimal v) => Math.Round((double)v, 0, MidpointRounding.ToEven).ToString("#,##0", Inv);

    public static string Comma0(double v) => Math.Round(v, 0, MidpointRounding.ToEven).ToString("#,##0", Inv);

    /// <summary>"{:,.2f}".format(x).</summary>
    public static string Comma2(decimal? v) => Math.Round((double)(v ?? 0), 2, MidpointRounding.ToEven).ToString("#,##0.00", Inv);
}
