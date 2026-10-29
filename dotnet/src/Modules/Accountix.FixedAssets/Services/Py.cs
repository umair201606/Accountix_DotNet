using System.Globalization;
using System.Numerics;
using System.Text;

namespace Accountix.FixedAssets.Services;

/// <summary>
/// Python's float arithmetic where the fixed-asset code relies on it, so the
/// figures (and the journals built from them) match the Python app to the
/// paisa: the depreciation engine runs on floats, rounds with round(x, n)
/// (correctly rounded, half-even on the exact binary value), adds with
/// Python 3.12's sum() (Neumaier-compensated), and prints with "{:,.0f}".
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>float(Decimal): the nearest double (Money columns come back as floats in Python).</summary>
    public static double F(decimal? d) => d is null ? 0.0 : double.Parse(d.Value.ToString(Inv), NumberStyles.Float, Inv);

    /// <summary>Decimal(str(x)): what Python writes for a float (journal lines, Money columns).</summary>
    public static decimal Dec(double x) =>
        x == 0 ? 0m : decimal.Parse(x.ToString("R", Inv), NumberStyles.Float, Inv);

    /// <summary>round(x, n) for a float: correctly rounded, ties to even on the exact value.</summary>
    public static double Round(double x, int nd) => Accountix.Core.Shared.PyCompat.Round(x, nd);

    /// <summary>sum(iterable) of floats in Python 3.12+: Neumaier compensated, starting from int 0.</summary>
    public static double Sum(IEnumerable<double> xs)
    {
        using var e = xs.GetEnumerator();
        if (!e.MoveNext())
            return 0.0;
        double f = 0.0 + e.Current, c = 0.0;
        while (e.MoveNext())
        {
            double x = e.Current, t = f + x;
            if (Math.Abs(f) >= Math.Abs(x))
                c += (f - t) + x;
            else
                c += (x - t) + f;
            f = t;
        }
        if (c != 0 && double.IsFinite(c))
            f += c;
        return f;
    }

    /// <summary>min(a, b): the first unless the second is smaller.</summary>
    public static double Min(double a, double b) => b < a ? b : a;

    /// <summary>max(a, b): the first unless the second is larger.</summary>
    public static double Max(double a, double b) => b > a ? b : a;

    /// <summary>"{:,.Nf}".format(x).</summary>
    public static string Comma(double x, int decimals = 0) => Fixed(x, decimals, grouping: true);

    public static string Comma(decimal? x, int decimals = 0) => Comma(F(x), decimals);

    /// <summary>"{:.Nf}".format(x).</summary>
    public static string Fixed(double x, int decimals, bool grouping = false)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        bool neg = x < 0 || (x == 0 && double.IsNegative(x));
        string digits = "0";
        if (x != 0)
        {
            var (_, q) = Scaled(x, decimals);
            digits = q.ToString(Inv);
        }
        string intPart = digits, frac = "";
        if (decimals > 0)
        {
            if (digits.Length <= decimals)
                digits = new string('0', decimals - digits.Length + 1) + digits;
            intPart = digits[..(digits.Length - decimals)];
            frac = digits[(digits.Length - decimals)..];
        }
        if (grouping) intPart = Group(intPart);
        return (neg ? "-" : "") + intPart + (decimals > 0 ? "." + frac : "");
    }

    private static string Group(string digits)
    {
        var sb = new StringBuilder();
        int lead = digits.Length % 3;
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (i - lead) % 3 == 0) sb.Append(',');
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    /// <summary>|x| × 10^nd rounded half-even on the exact binary value: (sign, integer).</summary>
    private static (bool Neg, BigInteger Q) Scaled(double x, int nd)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        exp -= 1075;
        BigInteger num = man, den = BigInteger.One;
        if (exp > 0) num <<= exp; else den <<= -exp;
        if (nd >= 0) num *= BigInteger.Pow(10, nd); else den *= BigInteger.Pow(10, -nd);
        var q = BigInteger.DivRem(num, den, out var r);
        int cmp = (r * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += 1;
        return (neg, q);
    }

    /// <summary>repr(float): shortest round-trip, Python's layout (1.0, 1e-05, 1e+16).</summary>
    public static string Repr(double x) => Accountix.Core.Shared.PyCompat.FloatRepr(x, json: true);

    /// <summary>A Quantity column as Python prints it: whole numbers "10", fractions as a float ("2.5").</summary>
    public static string Quantity(decimal? q)
    {
        double v = Round(F(q), 4);
        return v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(Inv) : Repr(v);
    }

    /// <summary>str(date) / "%d %b %Y".</summary>
    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);
    public static string DateLong(DateOnly d) => d.ToString("dd MMM yyyy", Inv);
}
