using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Accountix.Invoicing.Services;

/// <summary>
/// Python's float arithmetic, exactly. The invoice routes compute in floats
/// (round(x, 2) is half-even on the exact binary value; sum() of floats is
/// Neumaier-compensated since Python 3.12) and post Decimal(str(x)): the
/// journal lines are only identical to the paisa if every step matches.
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── exact decimal value of a double: digits × 10^exp ────────────────
    private static (bool Neg, BigInteger Digits, int Exp) Exact(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool neg = bits < 0;
        int e = (int)((bits >> 52) & 0x7FF);
        long m = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e = 1; else m |= 1L << 52;
        e -= 1075;                               // value = m × 2^e
        if (m == 0) return (neg, BigInteger.Zero, 0);
        if (e >= 0) return (neg, new BigInteger(m) << e, 0);
        // m × 2^e = m × 5^-e × 10^e
        return (neg, new BigInteger(m) * BigInteger.Pow(5, -e), e);
    }

    /// <summary>The exact value × 10^n rounded half-even to an integer (sign apart).</summary>
    private static (bool Neg, BigInteger Q) Scaled(double d, int n)
    {
        var (neg, digits, exp) = Exact(d);
        int shift = exp + n;
        if (shift >= 0) return (neg, digits * BigInteger.Pow(10, shift));
        var div = BigInteger.Pow(10, -shift);
        var q = BigInteger.DivRem(digits, div, out var r);
        var twice = r * 2;
        if (twice > div || (twice == div && !q.IsEven)) q += 1;
        return (neg, q);
    }

    /// <summary>round(x, n) — correctly rounded, half-even on the exact binary value.</summary>
    public static double Round(double x, int n = 0) => Accountix.Core.Shared.PyCompat.Round(x, n);

    /// <summary>f"{x:.nf}".</summary>
    public static string Fixed(double x, int n)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        var (neg, q) = Scaled(x, n);
        string s = q.ToString(Inv).PadLeft(n + 1, '0');
        string body = n > 0 ? s[..^n] + "." + s[^n..] : s;
        return (neg ? "-" : "") + body;
    }

    /// <summary>repr(float) / str(float): shortest round-trip, Python's notation.</summary>
    public static string Repr(double x) => Accountix.Core.Shared.PyCompat.FloatRepr(x);

    /// <summary>f"{x:g}" (6 significant digits, trailing zeros removed).</summary>
    public static string G(double x, int precision = 6)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        if (x == 0) return BitConverter.DoubleToInt64Bits(x) < 0 ? "-0" : "0";
        var (neg, digits, exp) = Exact(x);
        string ds = digits.ToString(Inv);
        int e10 = ds.Length - 1 + exp;                     // exponent of the first digit
        // round to `precision` significant digits
        var (_, q) = Scaled(Math.Abs(x), precision - 1 - e10);
        string qs = q.ToString(Inv);
        if (qs.Length > precision) { e10 += 1; qs = qs[..precision]; }
        string body;
        if (e10 < -4 || e10 >= precision)
        {
            string m = qs.TrimEnd('0');
            m = m.Length > 1 ? m[0] + "." + m[1..] : m;
            body = m + "e" + (e10 < 0 ? "-" : "+") + Math.Abs(e10).ToString("00", Inv);
        }
        else
        {
            int point = e10 + 1;
            string s = point <= 0 ? "0." + new string('0', -point) + qs
                     : point >= qs.Length ? qs + new string('0', point - qs.Length)
                     : qs[..point] + "." + qs[point..];
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            body = s;
        }
        return (neg ? "-" : "") + body;
    }

    /// <summary>sum(iterable) of floats, start 0: Neumaier-compensated as CPython 3.12.</summary>
    public static double Sum(IEnumerable<double> values)
    {
        using var it = values.GetEnumerator();
        if (!it.MoveNext()) return 0.0;
        double f = 0 + it.Current;   // int 0 + first float
        double c = 0.0;
        while (it.MoveNext())
        {
            double x = it.Current;
            double t = f + x;
            if (Math.Abs(f) >= Math.Abs(x)) c += (f - t) + x;
            else c += (x - t) + f;
            f = t;
        }
        if (c != 0 && double.IsFinite(c)) f += c;
        return f;
    }

    public static double Sum<T>(IEnumerable<T> items, Func<T, double> f) => Sum(items.Select(f));

    /// <summary>Decimal(str(x)): what Python posts and stores for a float.</summary>
    public static decimal Dec(double x)
    {
        if (!double.IsFinite(x)) return 0m;
        string r = Repr(x);
        return decimal.Parse(r, NumberStyles.Float, Inv);
    }

    /// <summary>float(Decimal): the float Python reads back from a NUMERIC column.</summary>
    public static double F(decimal? d) => d is decimal v ? ToDouble(v) : 0.0;

    /// <summary>A Money column as Python reads it (float), or null.</summary>
    public static double? FN(decimal? d) => d is decimal v ? ToDouble(v) : null;

    /// <summary>Correctly rounded decimal → double (the (double) cast can be one unit off).</summary>
    public static double ToDouble(decimal v) => double.Parse(v.ToString(Inv), NumberStyles.Float, Inv);

    /// <summary>shared/formatting.format_amount for a float value.</summary>
    public static string Amount(double? value, int decimals = 2, string? numberFormat = "en", bool brackets = true)
    {
        double v = value ?? 0;
        if (decimals < 0) decimals = 0;
        bool negative = v < 0;
        double mag = Math.Abs(v);
        string text;
        if (numberFormat == "hi")
        {
            double rounded = Round(mag, decimals);
            string fx = Fixed(rounded, decimals);
            string intPart = fx.Split('.')[0];
            // int(rounded) truncates; Fixed of the rounded value has the same integer part.
            text = GroupIndian(intPart) + (decimals > 0 ? "." + fx.Split('.')[1] : "");
        }
        else
        {
            string fx = Fixed(mag, decimals);
            string intPart = fx.Split('.')[0];
            text = GroupWestern(intPart) + (decimals > 0 ? "." + fx.Split('.')[1] : "");
        }
        if (!negative) return text;
        return brackets ? $"({text})" : $"-{text}";
    }

    public static string Amount(decimal? value, int decimals = 2, string? numberFormat = "en", bool brackets = true) =>
        Amount(value is null ? 0.0 : ToDouble(value.Value), decimals, numberFormat, brackets);

    private static string GroupWestern(string digits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(',');
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    private static string GroupIndian(string digits)
    {
        if (digits.Length <= 3) return digits;
        string head = digits[..^3], tail = digits[^3..];
        var groups = new List<string>();
        while (head.Length > 0)
        {
            groups.Insert(0, head.Length > 2 ? head[^2..] : head);
            head = head.Length > 2 ? head[..^2] : "";
        }
        return string.Join(",", groups) + "," + tail;
    }

    /// <summary>float(str): Python's parser (strips whitespace, accepts inf/nan, underscores between digits).</summary>
    public static bool TryParseFloat(string? s, out double value)
    {
        value = 0;
        if (s is null) return false;
        string t = s.Trim();
        if (t.Length == 0) return false;
        string low = t.ToLowerInvariant();
        string unsigned = low.TrimStart('+', '-');
        if (unsigned is "inf" or "infinity" or "nan")
        {
            bool neg = low.StartsWith('-');
            value = unsigned == "nan" ? double.NaN : (neg ? double.NegativeInfinity : double.PositiveInfinity);
            return true;
        }
        if (t.Contains("__") || t.StartsWith('_') || t.EndsWith('_')) return false;
        t = t.Replace("_", "");
        foreach (char ch in t)
            if (!(char.IsAsciiDigit(ch) || ch is '.' or 'e' or 'E' or '+' or '-')) return false;
        return double.TryParse(t, NumberStyles.Float, Inv, out value);
    }

    /// <summary>A whole-number quantity prints as an int ("10"), else as a float (the Quantity column type).</summary>
    public static string Qty(decimal? q)
    {
        double v = Round(F(q), 4);
        return v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(Inv) : Repr(v);
    }

    public static object QtyJson(decimal? q)
    {
        if (q is null) return null!;
        double v = Round(ToDouble(q.Value), 4);
        return v == Math.Floor(v) && Math.Abs(v) < 1e15 ? (object)(long)v : v;
    }
}

/// <summary>Python's json.dumps as Flask's jsonify / |tojson write it: sorted keys, compact, ASCII.</summary>
public static class PyJson
{
    /// <summary>
    /// jsonify writes compact JSON; the |tojson filter keeps json.dumps' default separators (", " and ": ")
    /// and makes it HTML-safe: pass <paramref name="htmlSafe"/> for that.
    /// </summary>
    public static string Dumps(object? value, bool htmlSafe = false)
    {
        var sb = new StringBuilder();
        Write(sb, value, htmlSafe ? ", " : ",", htmlSafe ? ": " : ":", sortKeys: true);
        string s = sb.ToString();
        if (htmlSafe)
            s = s.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("'", "\\u0027");
        return s;
    }

    /// <summary>Plain json.dumps(value): keys in insertion order, ", " and ": " separators, ASCII.</summary>
    public static string JsonDumps(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, ", ", ": ", sortKeys: false);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v, string comma, string colon, bool sortKeys)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case int or long or short: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(double.IsNaN(d) ? "NaN" : double.IsInfinity(d) ? (d > 0 ? "Infinity" : "-Infinity") : Py.Repr(d)); break;
            case float f: Write(sb, (double)f, comma, colon, sortKeys); break;
            case decimal m: sb.Append(Py.Repr(Py.ToDouble(m))); break;
            case JsonElement je: sb.Append(je.GetRawText()); break;
            case System.Collections.IDictionary dict:
            {
                var keys = dict.Keys.Cast<object>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)!).ToList();
                if (sortKeys) keys.Sort(string.CompareOrdinal);
                sb.Append('{');
                bool first = true;
                foreach (var k in keys)
                {
                    if (!first) sb.Append(comma);
                    first = false;
                    WriteString(sb, k);
                    sb.Append(colon);
                    Write(sb, dict[k], comma, colon, sortKeys);
                }
                sb.Append('}');
                break;
            }
            case System.Collections.IEnumerable list:
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(comma);
                    first = false;
                    Write(sb, item, comma, colon, sortKeys);
                }
                sb.Append(']');
                break;
            }
            default: WriteString(sb, v.ToString() ?? ""); break;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>A Flask jsonify answer.</summary>
    public static ContentResult Result(object? value, int status = 200) =>
        new() { Content = Dumps(value), ContentType = "application/json", StatusCode = status };
}

/// <summary>
/// Reads a JSON request body the way the Python routes read their dict:
/// data.get(key, default), float(...), Python truthiness.
/// </summary>
public sealed class PyData(JsonElement root)
{
    public JsonElement Root { get; } = root;

    public static async Task<PyData> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        // request.get_json(force=True): the body as JSON whatever the content type.
        using var reader = new StreamReader(request.Body);
        string body = await reader.ReadToEndAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return new PyData(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            throw new PyBadRequestException();
        }
    }

    public bool IsObject => Root.ValueKind == JsonValueKind.Object;

    public JsonElement? Get(string key) =>
        Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty(key, out var v) ? v : null;

    public bool Has(string key) => Get(key) is not null;

    public static bool Truthy(JsonElement? e) => e is { } v && v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => false,
    };

    public bool Truthy(string key) => Truthy(Get(key));

    /// <summary>float(x): numbers and numeric strings; None/other raises (Python's TypeError / ValueError).</summary>
    public static double ToFloat(JsonElement e, string key)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.True: return 1.0;
            case JsonValueKind.False: return 0.0;
            case JsonValueKind.String:
                if (Py.TryParseFloat(e.GetString(), out double d)) return d;
                throw new PyValueException($"could not convert string to float: '{e.GetString()}'");
            case JsonValueKind.Null:
                throw new PyValueException("float() argument must be a string or a real number, not 'NoneType'");
            default:
                throw new PyValueException($"float() argument must be a string or a real number, not '{e.ValueKind}'");
        }
    }

    /// <summary>float(data.get(key, default)).</summary>
    public double Float(string key, double @default = 0) => Get(key) is { } e ? ToFloat(e, key) : @default;

    /// <summary>float(data.get(key, default) or 0).</summary>
    public double FloatOr0(string key) => Get(key) is { } e && Truthy(e) ? ToFloat(e, key) : 0.0;

    /// <summary>data.get(key) or None, as an id.</summary>
    public int? Id(string key) => ToId(Get(key));

    public static int? ToId(JsonElement? e)
    {
        if (!Truthy(e)) return null;
        var v = e!.Value;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out int i)) return i;
            double d = v.GetDouble();
            return (int)d;
        }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString()!.Trim(), out int s)) return s;
        if (v.ValueKind == JsonValueKind.True) return 1;
        throw new PyValueException($"invalid literal for int() with base 10: {v.GetRawText()}");
    }

    /// <summary>data.get(key, default) as text (None stays null).</summary>
    public string? Str(string key, string? @default = "")
    {
        if (Get(key) is not { } e) return @default;
        return e.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => e.GetRawText(),
        };
    }

    /// <summary>bool(data.get(key, default)).</summary>
    public bool Bool(string key, bool @default = false) => Get(key) is { } e ? Truthy(e) : @default;

    public IEnumerable<PyData> List(string key)
    {
        if (Get(key) is { ValueKind: JsonValueKind.Array } arr)
            foreach (var x in arr.EnumerateArray())
                yield return new PyData(x);
    }
}

/// <summary>Python raised TypeError/ValueError reading the payload: the route answers 500 "Server error".</summary>
public sealed class PyValueException(string message) : Exception(message);

/// <summary>get_json(force=True) on a body that is not JSON: 400.</summary>
public sealed class PyBadRequestException() : Exception("Failed to decode JSON object");
