using System.Globalization;
using System.Numerics;

namespace Accountix.Executive.Services;

/// <summary>
/// Python's number semantics where the executive pages print or compare what
/// Python computed in floats: <c>float(Decimal)</c>, <c>Decimal(str(float))</c>,
/// <c>round(x, n)</c>, <c>repr(float)</c> and the <c>{:,.2f}</c> / <c>str(Decimal)</c>
/// formats. Figures are held as decimal; these keep the printed text and the
/// chart geometry identical to the Python app's.
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>_q: Decimal quantized to 0.01, ROUND_HALF_UP.</summary>
    public static decimal Q(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>float(Decimal): the nearest double (correctly rounded, unlike a plain cast).</summary>
    public static double ToDouble(decimal v) => double.Parse(v.ToString(Inv), NumberStyles.Float, Inv);

    /// <summary>Decimal(str(x)) for a float: the shortest repr, exactly.</summary>
    public static decimal FromDouble(double v) => decimal.Parse(v.ToString("R", Inv), NumberStyles.Float, Inv);

    /// <summary>_f(float): Decimal(str(x)).quantize(0.01, ROUND_HALF_UP).</summary>
    public static decimal F(double v) => Q(FromDouble(v));

    /// <summary>repr(float) / str(float): shortest round-trip digits, ".0" on whole numbers.</summary>
    public static string Repr(double v) => Accountix.Core.Shared.PyCompat.FloatRepr(v);

    /// <summary>str() of a value Python holds as a float, given as decimal (a 2dp figure).</summary>
    public static string Repr(decimal v) => Repr(ToDouble(v));

    /// <summary>round(x, n) on a float: the exact binary value rounded half-even, as CPython does.</summary>
    public static double Round(double x, int digits) => Accountix.Core.Shared.PyCompat.Round(x, digits);

    /// <summary>round(x) → int: half-even on the exact value.</summary>
    public static long RoundInt(double x) => (long)Math.Round(x, MidpointRounding.ToEven);

    public static long RoundInt(decimal x) => (long)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>f"{d:,.2f}" for a Decimal (ROUND_HALF_EVEN).</summary>
    public static string Comma2(decimal v) =>
        Math.Round(v, 2, MidpointRounding.ToEven).ToString("#,##0.00;-#,##0.00", Inv);

    /// <summary>str(Decimal): the stored digits and scale, Python's notation.</summary>
    public static string DecStr(decimal v) => v.ToString(Inv);

    /// <summary>str(Decimal.normalize()): trailing zeros dropped; 1500 → "1.5E+3", as Python prints it.</summary>
    public static string Normalized(decimal v)
    {
        if (v == 0) return "0";
        string s = Math.Abs(v).ToString(Inv);
        string intPart = s, fracPart = "";
        int dot = s.IndexOf('.');
        if (dot >= 0) { intPart = s[..dot]; fracPart = s[(dot + 1)..].TrimEnd('0'); }
        string sign = v < 0 ? "-" : "";
        if (fracPart.Length > 0)
            return sign + intPart + "." + fracPart;
        // A whole number: coefficient without trailing zeros, exponent = zeros dropped.
        string coeff = intPart.TrimEnd('0');
        int zeros = intPart.Length - coeff.Length;
        if (zeros == 0) return sign + intPart;
        string c = coeff.Length > 1 ? coeff[0] + "." + coeff[1..] : coeff;
        int adjusted = intPart.Length - 1;
        return $"{sign}{c}E+{adjusted}";
    }

    /// <summary>The Quantity column type's value as Python prints it: int when whole, else round(float, 4).</summary>
    public static string Quantity(decimal? v)
    {
        if (v is null) return "None";
        double r = Round(ToDouble(v.Value), 4);
        if (r == Math.Floor(r) && Math.Abs(r) < 1e15) return ((long)r).ToString(Inv);
        return Repr(r);
    }
}
