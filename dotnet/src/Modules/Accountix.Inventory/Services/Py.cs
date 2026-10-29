using System.Globalization;

namespace Accountix.Inventory.Services;

/// <summary>Python's text forms of numbers, where the app prints them into data (journal text, messages).</summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>str(Decimal(x).normalize()): trailing zeros dropped, and 50 → "5E+1" as Python prints it.</summary>
    public static string Normalize(decimal d)
    {
        if (d == 0) return "0";
        bool neg = d < 0;
        string plain = Math.Abs(d).ToString(Inv);          // no exponent, all significant digits
        string intPart = plain, frac = "";
        int dot = plain.IndexOf('.');
        if (dot >= 0) { intPart = plain[..dot]; frac = plain[(dot + 1)..].TrimEnd('0'); }
        string digits = (intPart + frac).TrimStart('0');
        int exponent = -frac.Length;
        // strip trailing zeros of the coefficient into the exponent
        int stripped = digits.Length - digits.TrimEnd('0').Length;
        digits = digits.TrimEnd('0');
        exponent += stripped;
        if (digits.Length == 0) return "0";
        int adjusted = exponent + digits.Length - 1;
        string body;
        if (exponent <= 0 && adjusted >= -6)
        {
            if (exponent == 0) body = digits;
            else
            {
                int point = digits.Length + exponent;
                body = point > 0 ? digits[..point] + "." + digits[point..] : "0." + new string('0', -point) + digits;
            }
        }
        else
        {
            body = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
            body += "E" + (adjusted >= 0 ? "+" : "-") + Math.Abs(adjusted).ToString(Inv);
        }
        return neg ? "-" + body : body;
    }

    /// <summary>str(Decimal) of a value as stored (Numeric(16,4) reads back with 4 places: "10.0000").</summary>
    public static string Dec(decimal d) => d.ToString(Inv);

    /// <summary>f"{x:g}" for a float (6 significant digits, "1.23457e+06").</summary>
    public static string G(double d) => d.ToString("G6", Inv).ToLowerInvariant();

    /// <summary>Decimal(str(float(x))): what the voucher routes hand the engine (4 → 4.0, 2.5 → 2.5).</summary>
    public static decimal FloatDecimal(decimal x)
    {
        string r = ((double)x).ToString("R", Inv);
        if (!r.Contains('.') && !r.Contains('E'))
            r += ".0";
        return decimal.Parse(r, NumberStyles.Float, Inv);
    }

    public static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
