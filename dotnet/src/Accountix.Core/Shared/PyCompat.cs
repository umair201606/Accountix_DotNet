using System.Globalization;
using System.Numerics;

namespace Accountix.Core.Shared;

/// <summary>
/// The two number rules the data depends on, exactly as the former Python app applied them: figures it
/// stored and printed (journal descriptions, payloads, slips, audit rows, golden fixtures) were made with
/// Python's round() and repr(float), so the .NET app produces the same bytes with these. One copy for every
/// module (each module's own Py helpers delegate here).
/// </summary>
public static class PyCompat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// round(float, ndigits): the exact binary value of <paramref name="x"/> rounded half-even to
    /// <paramref name="ndigits"/> decimal places (negative: tens, hundreds…), then the nearest double.
    /// So 2.675 → 2.67 (its binary value is 2.67499…) and 0.125 → 0.12, 2.5 → 2.0, 1250 at -2 → 1200.
    /// </summary>
    public static double Round(double x, int ndigits = 0)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0)
            return x;
        if (ndigits > 330)
            return x;                                   // finer than any double: unchanged, as Python
        if (ndigits < -330)
            return double.IsNegative(x) ? -0.0 : 0.0;
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;                               // |x| = mantissa × 2^exponent, exactly
        BigInteger num = mantissa, den = BigInteger.One;
        if (exponent > 0) num <<= exponent; else den <<= -exponent;
        if (ndigits >= 0) num *= BigInteger.Pow(10, ndigits); else den *= BigInteger.Pow(10, -ndigits);
        var q = BigInteger.DivRem(num, den, out var r);
        int c = (r * 2).CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven))
            q += 1;
        if (q.IsZero)
            return negative ? -0.0 : 0.0;
        string digits = q.ToString(Inv);
        string text = ndigits <= 0 ? digits + new string('0', -ndigits)
            : digits.Length > ndigits ? digits[..^ndigits] + "." + digits[^ndigits..]
            : "0." + digits.PadLeft(ndigits, '0');
        double v = double.Parse(text, NumberStyles.Float, Inv);
        return negative ? -v : v;
    }

    /// <summary>
    /// repr(float) / str(float): the shortest digits that read back as the same double, in Python's layout
    /// ("0.1", "1e-05", "1e+16", "100.0", "-0.0"). With <paramref name="json"/>, non-finite values are written
    /// as json.dumps writes them ("NaN", "Infinity", "-Infinity").
    /// </summary>
    public static string FloatRepr(double x, bool json = false)
    {
        if (double.IsNaN(x)) return json ? "NaN" : "nan";
        if (double.IsPositiveInfinity(x)) return json ? "Infinity" : "inf";
        if (double.IsNegativeInfinity(x)) return json ? "-Infinity" : "-inf";
        string sign = double.IsNegative(x) ? "-" : "";
        if (x == 0) return sign + "0.0";
        // .NET's "R" is the shortest round-trip digit string; only the layout differs from Python's.
        string r = Math.Abs(x).ToString("R", Inv);
        int e = 0;
        int ePos = r.IndexOfAny(['E', 'e']);
        string mant = r;
        if (ePos >= 0)
        {
            e = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, Inv);
            mant = r[..ePos];
        }
        int dot = mant.IndexOf('.');
        string digits = dot < 0 ? mant : mant.Remove(dot, 1);
        int decpt = (dot < 0 ? mant.Length : dot) + e;  // value = 0.digits × 10^decpt
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        decpt -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return sign + "0.0";
        string body;
        if (decpt <= -4 || decpt > 16)
        {
            int exp = decpt - 1;
            string m = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            body = m + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", Inv);
        }
        else if (decpt <= 0)
            body = "0." + new string('0', -decpt) + digits;
        else if (decpt >= digits.Length)
            body = digits + new string('0', decpt - digits.Length) + ".0";
        else
            body = digits[..decpt] + "." + digits[decpt..];
        return sign + body;
    }
}
