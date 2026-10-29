using System.Globalization;
using System.Numerics;
using System.Text;

namespace Accountix.HrRecords.Services;

/// <summary>
/// How Python and Jinja print values, so pages and JSON read byte for byte
/// as the Python app's: float repr ("7.0", "1e-05"), round() on floats
/// (exact, half to even), str(date), strftime patterns, str.title().
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>repr(float): shortest round-trip digits, fixed notation for 1e-4 ≤ |x| &lt; 1e16, else '1e+16' / '1e-05'.</summary>
    public static string Float(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        var (negative, digits, decpt) = ShortestDigits(d);
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0)
                sb.Append("0.").Append('0', -decpt).Append(digits);
            else if (decpt >= digits.Length)
                sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
            else
                sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
        else
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            int exp = decpt - 1;
            sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", Inv));
        }
        return sb.ToString();
    }

    /// <summary>The shortest digits that round-trip and the decimal point position (value = 0.DIGITS × 10^decpt).</summary>
    private static (bool Negative, string Digits, int Decpt) ShortestDigits(double d)
    {
        string r = Math.Abs(d).ToString("R", Inv);
        string digits;
        int decpt;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            string mant = r[..e];
            int exp = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, Inv);
            int dot = mant.IndexOf('.');
            digits = mant.Replace(".", "");
            decpt = (dot < 0 ? mant.Length : dot) + exp;
        }
        else
        {
            int dot = r.IndexOf('.');
            string ip = dot < 0 ? r : r[..dot];
            string fp = dot < 0 ? "" : r[(dot + 1)..];
            string all = ip + fp;
            int lead = 0;
            while (lead < all.Length - 1 && all[lead] == '0') lead++;
            digits = all[lead..];
            decpt = ip.Length - lead;
        }
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        return (d < 0, digits, decpt);
    }

    /// <summary>
    /// Python's round(x, n) on a float: the exact binary value rounded half to
    /// even at n decimals, then the nearest double (Math.Round scales by 10^n
    /// first, which can land on a false tie).
    /// </summary>
    public static double Round(double x, int n) => Accountix.Core.Shared.PyCompat.Round(x, n);

    /// <summary>'%g' % x / format(x, 'g'): six significant digits, no trailing zeros.</summary>
    public static string G(double d)
    {
        if (d == 0) return double.IsNegative(d) ? "-0" : "0";
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        // Round to 6 significant digits (exact, half even), then choose fixed or exponent as C's %g.
        int exp10 = (int)Math.Floor(Math.Log10(Math.Abs(d)));
        double rounded = Round(d, 5 - exp10);
        if (rounded != 0)
            exp10 = (int)Math.Floor(Math.Log10(Math.Abs(rounded)));
        if (exp10 < -4 || exp10 >= 6)
        {
            string m = (rounded / Math.Pow(10, exp10)).ToString("0.#####", Inv);
            return $"{m}e{(exp10 < 0 ? '-' : '+')}{Math.Abs(exp10):00}";
        }
        int decimals = Math.Max(0, 5 - exp10);
        string f = rounded.ToString("F" + decimals, Inv);
        if (f.Contains('.')) f = f.TrimEnd('0').TrimEnd('.');
        return f;
    }

    /// <summary>str(date): 2026-10-29; None prints "None" (Jinja prints None as the word).</summary>
    public static string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", Inv) ?? "None";

    /// <summary>date.isoformat() of a nullable date for JSON (null stays null).</summary>
    public static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", Inv);

    /// <summary>datetime.isoformat(): '2026-10-29T09:30:00', microseconds when present.</summary>
    public static string Iso(DateTime d)
    {
        string s = d.ToString("yyyy-MM-ddTHH:mm:ss", Inv);
        long micro = d.Ticks % TimeSpan.TicksPerSecond / 10;
        return micro == 0 ? s : s + "." + micro.ToString("D6", Inv);
    }

    /// <summary>strftime('%d-%b-%Y %H:%M').</summary>
    public static string DMonYHm(DateTime? d) => d?.ToString("dd-MMM-yyyy HH:mm", Inv) ?? "";

    /// <summary>strftime('%d-%b-%Y').</summary>
    public static string DMonY(DateTime? d) => d?.ToString("dd-MMM-yyyy", Inv) ?? "";

    /// <summary>strftime('%H:%M').</summary>
    public static string Hm(TimeOnly t) => t.ToString("HH:mm", Inv);

    public static string Hm(DateTime d) => d.ToString("HH:mm", Inv);

    /// <summary>s[:n].</summary>
    public static string Cut(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n];

    /// <summary>str.title(): a letter after a non-letter starts a word (upper), the rest lower.</summary>
    public static string Title(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = s.ToCharArray();
        bool prevCased = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (char.IsLetter(c))
            {
                chars[i] = prevCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
                prevCased = true;
            }
            else
                prevCased = false;
        }
        return new string(chars);
    }

    /// <summary>
    /// datetime.strptime(value, "%Y-%m-%d").date(): four-digit year, month and
    /// day with or without a leading zero (Python's pattern), a real date.
    /// </summary>
    public static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (value is null) return false;
        var m = System.Text.RegularExpressions.Regex.Match(value,
            @"\A(\d{4})-(1[0-2]|0[1-9]|[1-9])-(3[01]|[12]\d|0[1-9]|[1-9]| [1-9])\z");
        if (!m.Success) return false;
        int y = int.Parse(m.Groups[1].Value, Inv), mo = int.Parse(m.Groups[2].Value, Inv), d = int.Parse(m.Groups[3].Value.Trim(), Inv);
        if (y < 1 || d > DateTime.DaysInMonth(y, mo)) return false;
        date = new DateOnly(y, mo, d);
        return true;
    }

    /// <summary>datetime.strptime(value, "%H:%M").time(): hour 0–23 and minute 0–59, one or two digits.</summary>
    public static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;
        if (value is null) return false;
        var m = System.Text.RegularExpressions.Regex.Match(value, @"\A(2[0-3]|[0-1]\d|\d):([0-5]\d|\d)\z");
        if (!m.Success) return false;
        time = new TimeOnly(int.Parse(m.Groups[1].Value, Inv), int.Parse(m.Groups[2].Value, Inv));
        return true;
    }

    /// <summary>request.form.get(name, type=int) / int(text): Python's int() of a string (spaces, sign, underscores between digits).</summary>
    public static int? Int(string? s)
    {
        if (s is null) return null;
        string t = s.Trim();
        if (t.Length == 0) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"\A[+-]?\d+(_\d+)*\z")) return null;
        return int.TryParse(t.Replace("_", ""), NumberStyles.AllowLeadingSign, Inv, out int v) ? v : null;
    }

    /// <summary>Jinja's |int filter: int(value), else int(float(value)), else 0.</summary>
    public static int JinjaInt(string? s)
    {
        if (Int(s) is int i) return i;
        if (s is not null && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return (int)Math.Truncate(d);
        return 0;
    }
}
