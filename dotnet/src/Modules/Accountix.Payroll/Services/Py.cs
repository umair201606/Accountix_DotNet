using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Accountix.Payroll.Services;

/// <summary>
/// Python's float arithmetic, exactly. The payroll engine (hr_app/payroll_engine.py
/// and _preview_employee) computes in binary floats and rounds with round(x, 2),
/// then the journal takes Decimal(str(x)). Doing the same steps on doubles, with
/// Python's own rounding, sum() and repr, gives the same figures to the paisa.
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>float(value or 0) for a NUMERIC column (Python's Money type hands back float(Decimal), correctly rounded).</summary>
    public static double F(decimal? value) =>
        value is decimal d ? double.Parse(d.ToString(Inv), NumberStyles.Float, Inv) : 0.0;

    /// <summary>Decimal(str(x)): what post_journal_entry and the Money columns store for a float.</summary>
    public static decimal Dec(double x) => decimal.Parse(Repr(x) switch
    {
        "inf" or "-inf" or "nan" => throw new OverflowException($"{x} is not a number"),
        var s => s,
    }, NumberStyles.Float, Inv);

    /// <summary>round(x, n): round half to even on the exact binary value, then the nearest double (CPython double_round).</summary>
    public static double Round(double x, int n = 2) => Accountix.Core.Shared.PyCompat.Round(x, n);

    /// <summary>x × 10^n rounded half-even to an integer, exactly (and the sign).</summary>
    private static (BigInteger Q, bool Negative) Scaled(double x, int n)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        exp -= 1075;
        BigInteger num = mant, den = BigInteger.One;
        if (exp > 0) num <<= exp; else den <<= -exp;
        if (n >= 0) num *= BigInteger.Pow(10, n); else den *= BigInteger.Pow(10, -n);
        var q = BigInteger.DivRem(num, den, out var rem);
        int cmp = (rem * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += 1;
        return (q, neg);
    }

    /// <summary>f"{x:,.{d}f}" (comma = true) or f"{x:.{d}f}": correctly rounded, half to even.</summary>
    public static string Fixed(double x, int decimals = 0, bool comma = true)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        var (q, neg) = x == 0 ? (BigInteger.Zero, double.IsNegative(x)) : Scaled(x, decimals);
        string digits = q.ToString(Inv).PadLeft(decimals + 1, '0');
        string intPart = digits[..(digits.Length - decimals)];
        string frac = digits[(digits.Length - decimals)..];
        if (comma)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < intPart.Length; i++)
            {
                if (i > 0 && (intPart.Length - i) % 3 == 0) sb.Append(',');
                sb.Append(intPart[i]);
            }
            intPart = sb.ToString();
        }
        return (neg ? "-" : "") + intPart + (decimals > 0 ? "." + frac : "");
    }

    /// <summary>shared/formatting.format_amount on a float: western or Indian grouping, negatives in brackets.</summary>
    public static string Amount(double value, int decimals = 2, string? numberFormat = "en")
    {
        if (decimals < 0) decimals = 0;
        bool negative = value < 0;
        double magnitude = Math.Abs(value);
        string text;
        if (numberFormat == "hi")
        {
            double rounded = Round(magnitude, decimals);
            string whole = Fixed(Math.Truncate(rounded), 0, comma: false);
            string grouped = whole;
            if (whole.Length > 3)
            {
                string head = whole[..^3], tail = whole[^3..];
                var groups = new List<string>();
                while (head.Length > 0)
                {
                    groups.Insert(0, head.Length > 2 ? head[^2..] : head);
                    head = head.Length > 2 ? head[..^2] : "";
                }
                grouped = string.Join(",", groups) + "," + tail;
            }
            text = decimals > 0 ? grouped + "." + Fixed(rounded, decimals, comma: false).Split('.')[1] : grouped;
        }
        else
            text = Fixed(magnitude, decimals);
        return negative ? $"({text})" : text;
    }

    /// <summary>sum() of floats in Python 3.12+: Neumaier-compensated (CPython builtin_sum_impl).</summary>
    public static double Sum(IEnumerable<double> values)
    {
        double f = 0.0, c = 0.0;
        foreach (double x in values)
        {
            double t = f + x;
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

    /// <summary>min(a, b, ...): the first smallest (Python keeps the current item unless a later one is smaller).</summary>
    public static double Min(double a, params double[] rest)
    {
        double m = a;
        foreach (double x in rest)
            if (x < m) m = x;
        return m;
    }

    /// <summary>max(a, b): a unless b is larger.</summary>
    public static double Max(double a, double b) => b > a ? b : a;

    /// <summary>repr(float): the shortest digits that round-trip, in Python's layout (12500.0, 0.0001, 1e-05, 1e+16).</summary>
    public static string Repr(double x) => Accountix.Core.Shared.PyCompat.FloatRepr(x);

    /// <summary>float(s) for a form value: None when it cannot be read (request.form.get(..., type=float)).</summary>
    public static double? ParseFloat(string? raw)
    {
        if (raw is null) return null;
        string s = raw.Trim().Replace("_", "");
        if (s.Length == 0) return null;
        string low = s.ToLowerInvariant().TrimStart('+');
        bool negative = low.StartsWith('-');
        string bare = negative ? low[1..] : low;
        if (bare is "inf" or "infinity") return negative ? double.NegativeInfinity : double.PositiveInfinity;
        if (bare == "nan") return double.NaN;
        if (s.Any(ch => !(char.IsAsciiDigit(ch) || ch is '.' or 'e' or 'E' or '+' or '-')))
            return null;
        return double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : null;
    }

    /// <summary>int(s) for a form value: None when it cannot be read (request.form.get(..., type=int)).</summary>
    public static int? ParseInt(string? raw)
    {
        if (raw is null) return null;
        string s = raw.Trim().Replace("_", "");
        if (s.Length == 0) return null;
        return int.TryParse(s, NumberStyles.AllowLeadingSign, Inv, out int v) ? v : null;
    }

    /// <summary>str.title(): each run of letters starts upper case, the rest lower ("salary_advance" → "Salary_Advance").</summary>
    public static string Title(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool prevCased = false;
        foreach (char ch in s)
        {
            if (char.IsLetter(ch))
            {
                sb.Append(prevCased ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch));
                prevCased = true;
            }
            else
            {
                sb.Append(ch);
                prevCased = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>A Python number in a JSON text: an int or a float (written as repr).</summary>
    public readonly record struct Num(double Value, bool IsInt = false)
    {
        public static implicit operator Num(double v) => new(v);
        public static Num Int(long v) => new(v, true);
        public override string ToString() => IsInt ? ((long)Value).ToString(Inv) : Repr(Value);
    }

    /// <summary>
    /// json.dumps(obj) with Python's defaults (", " and ": " separators, ensure_ascii,
    /// floats as repr) for the dicts payroll stores in components_json.
    /// </summary>
    public static string Dumps(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case Num n: sb.Append(n.IsInt ? n.ToString() : JsonFloat(n.Value)); break;
            case double d: sb.Append(JsonFloat(d)); break;
            case int i: sb.Append(i.ToString(Inv)); break;
            case long l: sb.Append(l.ToString(Inv)); break;
            case IEnumerable<KeyValuePair<string, Num>> numMap: WriteMap(sb, numMap.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))); break;
            case IEnumerable<KeyValuePair<string, object?>> map: WriteMap(sb, map); break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                bool first = true;
                foreach (object? item in list)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                break;
            default: WriteString(sb, value.ToString() ?? ""); break;
        }
    }

    private static void WriteMap(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> map)
    {
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in map)
        {
            if (!first) sb.Append(", ");
            first = false;
            WriteString(sb, k);
            sb.Append(": ");
            Write(sb, v);
        }
        sb.Append('}');
    }

    private static string JsonFloat(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : Repr(d);

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch > 0x7E)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", Inv));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>float(v) for a JSON number or numeric string (json.loads gives either); null when it is neither.</summary>
    public static double? JsonNumber(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => ParseFloat(e.GetString()),
        JsonValueKind.True => 1.0,
        JsonValueKind.False => 0.0,
        _ => null,
    };
}
