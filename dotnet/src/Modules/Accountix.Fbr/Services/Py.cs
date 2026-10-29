using System.Globalization;
using System.Numerics;
using System.Text;

namespace Accountix.Fbr.Services;

/// <summary>
/// A Python dict: string keys in insertion order (json.loads keeps the
/// document's order; a repeated key keeps its first place and its last value).
/// </summary>
public sealed class PyDict : OrderedDictionary<string, object?>
{
    public PyDict() : base(StringComparer.Ordinal) { }

    /// <summary>dict.get(key, default).</summary>
    public object? Get(string key, object? fallback = null) => TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>
/// Python's values as the FBR payload and FBR's answers need them, so .NET
/// writes byte for byte what the Python app writes:
/// <list type="bullet">
/// <item><c>double</c> is a Python float, <c>long</c>/<c>BigInteger</c> an int — json.dumps prints them
/// differently ("100.0" against "100");</item>
/// <item><see cref="Round"/> is Python's round(x, n): the exact binary value rounded half-even;</item>
/// <item><see cref="Float"/> is float(Decimal): the nearest double to the database's decimal;</item>
/// <item><see cref="Dumps"/> is json.dumps (ensure_ascii, Python's float repr, optional indent / sort_keys);</item>
/// <item><see cref="Loads"/> is json.loads; <see cref="Repr"/> is repr()/str() of what it returns.</item>
/// </list>
/// </summary>
public static class Py
{
    // ── Numbers ───────────────────────────────────────────────────────────

    /// <summary>float(value) of a NUMERIC read from the database (Decimal → float is correctly rounded).</summary>
    public static double Float(decimal value) => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>float(x or 0).</summary>
    public static double Float0(decimal? value) => value is decimal d ? Float(d) : 0.0;

    /// <summary>A Money column read by SQLAlchemy, then <c>x or fallback</c>: the float when non-zero, else the int fallback.</summary>
    public static object OrInt(decimal? value, long fallback) => value is decimal d && d != 0 ? (object)Float(d) : fallback;

    /// <summary>Python truthiness of a number (0, 0.0 and -0.0 are false).</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0.0,
        BigInteger b => !b.IsZero,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    private static bool IsInt(object v) => v is long or int or BigInteger;
    private static double D(object v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        BigInteger b => (double)b,
        _ => throw new InvalidCastException($"Not a number: {v}"),
    };
    private static BigInteger B(object v) => v switch { long l => l, int i => i, BigInteger b => b, _ => throw new InvalidCastException() };
    private static object Norm(BigInteger b) => b >= long.MinValue && b <= long.MaxValue ? (long)b : b;

    /// <summary>a * b (int * int stays int; anything with a float is a float).</summary>
    public static object Mul(object a, object b) => IsInt(a) && IsInt(b) ? Norm(B(a) * B(b)) : D(a) * D(b);
    public static object Add(object a, object b) => IsInt(a) && IsInt(b) ? Norm(B(a) + B(b)) : D(a) + D(b);
    public static object Sub(object a, object b) => IsInt(a) && IsInt(b) ? Norm(B(a) - B(b)) : D(a) - D(b);
    /// <summary>a / b: Python's true division, always a float.</summary>
    public static double Div(object a, object b) => D(a) / D(b);
    public static bool GreaterThanZero(object a) => IsInt(a) ? B(a) > 0 : D(a) > 0;

    /// <summary>round(x, ndigits): an int stays an int; a float is rounded as Python does.</summary>
    public static object Round(object x, int ndigits) => x is double d ? Round(d, ndigits) : x;

    /// <summary>
    /// Python's round(float, ndigits) (floatobject.c double_round): the exact
    /// binary value of <paramref name="x"/> rounded half-even to ndigits
    /// places, then the nearest double to that decimal. So 2.675 → 2.67 (its
    /// binary value is 2.67499…) and 0.125 → 0.12, exactly as Python.
    /// </summary>
    public static double Round(double x, int ndigits) => Accountix.Core.Shared.PyCompat.Round(x, ndigits);

    /// <summary>repr(float): the shortest digits that read back as the same double, in Python's layout.</summary>
    public static string FloatRepr(double x) => Accountix.Core.Shared.PyCompat.FloatRepr(x);

    // ── json.dumps ────────────────────────────────────────────────────────

    /// <summary>
    /// json.dumps(value, indent=…, sort_keys=…, separators=…): ensure_ascii
    /// as Python's default. With an indent the item separator is "," and the
    /// key separator ": "; without, ", " and ": " unless given.
    /// </summary>
    public static string Dumps(object? value, int? indent = null, bool sortKeys = false, string? itemSeparator = null, string? keySeparator = null)
    {
        var sb = new StringBuilder();
        string item = itemSeparator ?? (indent is null ? ", " : ",");
        string key = keySeparator ?? ": ";
        Write(sb, value, indent, sortKeys, item, key, 0);
        return sb.ToString();
    }

    /// <summary>Flask's jsonify body: sorted keys, compact, ensure_ascii, and a closing newline.</summary>
    public static string Jsonify(object? value) => Dumps(value, null, true, ",", ":") + "\n";

    private static void Write(StringBuilder sb, object? v, int? indent, bool sortKeys, string item, string key, int level)
    {
        switch (v)
        {
            case null: sb.Append("null"); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case string s: WriteString(sb, s); return;
            case double d:
                sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : FloatRepr(d));
                return;
            case float f: Write(sb, (double)f, indent, sortKeys, item, key, level); return;
            case long or int or short or BigInteger:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            case decimal m: sb.Append(FloatRepr(Float(m))); return;
            case PyDict dict:
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                var pairs = sortKeys ? dict.OrderBy(p => p.Key, StringComparer.Ordinal).ToList() : dict.ToList();
                sb.Append('{');
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (i > 0) sb.Append(item);
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, pairs[i].Key);
                    sb.Append(key);
                    Write(sb, pairs[i].Value, indent, sortKeys, item, key, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            case System.Collections.IList list:
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(item);
                    NewLine(sb, indent, level + 1);
                    Write(sb, list[i], indent, sortKeys, item, key, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            default:
                // json.dumps(..., default=str)
                WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
                return;
        }
    }

    private static void NewLine(StringBuilder sb, int? indent, int level)
    {
        if (indent is int n)
            sb.Append('\n').Append(' ', n * level);
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
                    if (c < ' ' || c > '~')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ── json.loads ────────────────────────────────────────────────────────

    /// <summary>
    /// json.loads: objects → <see cref="PyDict"/>, arrays → List, integers →
    /// long (BigInteger when larger), other numbers → double. Throws
    /// <see cref="FormatException"/> where Python raises ValueError.
    /// </summary>
    public static object? Loads(string text)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                MaxDepth = 1000,
            });
            return From(doc.RootElement);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new FormatException(e.Message, e);
        }
    }

    private static object? From(System.Text.Json.JsonElement e)
    {
        switch (e.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var d = new PyDict();
                foreach (var p in e.EnumerateObject())
                    d[p.Name] = From(p.Value);
                return d;
            case System.Text.Json.JsonValueKind.Array:
                return e.EnumerateArray().Select(From).ToList();
            case System.Text.Json.JsonValueKind.String:
                return e.GetString();
            case System.Text.Json.JsonValueKind.Number:
                string raw = e.GetRawText();
                if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                    return Norm(BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            case System.Text.Json.JsonValueKind.True: return true;
            case System.Text.Json.JsonValueKind.False: return false;
            default: return null;
        }
    }

    // ── repr / str ────────────────────────────────────────────────────────

    /// <summary>str(value): a str is itself, everything else its repr.</summary>
    public static string Str(object? v) => v is string s ? s : Repr(v);

    /// <summary>repr() of a value json.loads returns (dict, list, str, int, float, bool, None).</summary>
    public static string Repr(object? v)
    {
        var sb = new StringBuilder();
        WriteRepr(sb, v);
        return sb.ToString();
    }

    private static void WriteRepr(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("None"); return;
            case bool b: sb.Append(b ? "True" : "False"); return;
            case string s: StrRepr(sb, s); return;
            case double d: sb.Append(FloatRepr(d)); return;
            case long or int or BigInteger: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            case PyDict dict:
                sb.Append('{');
                bool first = true;
                foreach (var (k, val) in dict)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    StrRepr(sb, k);
                    sb.Append(": ");
                    WriteRepr(sb, val);
                }
                sb.Append('}');
                return;
            case System.Collections.IList list:
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteRepr(sb, list[i]);
                }
                sb.Append(']');
                return;
            default: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
        }
    }

    /// <summary>unicode_repr: single quotes unless the text has ' and no ", escapes as CPython.</summary>
    private static void StrRepr(StringBuilder sb, string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        sb.Append(quote);
        foreach (var rune in s.EnumerateRunes())
        {
            int ch = rune.Value;
            if (ch == quote || ch == '\\') { sb.Append('\\').Append((char)ch); continue; }
            if (ch == '\t') { sb.Append("\\t"); continue; }
            if (ch == '\n') { sb.Append("\\n"); continue; }
            if (ch == '\r') { sb.Append("\\r"); continue; }
            if (ch < ' ' || ch == 0x7F) { sb.Append("\\x").Append(ch.ToString("x2", CultureInfo.InvariantCulture)); continue; }
            if (ch < 0x7F) { sb.Append((char)ch); continue; }
            if (IsPrintable(rune)) { sb.Append(rune.ToString()); continue; }
            if (ch <= 0xFF) sb.Append("\\x").Append(ch.ToString("x2", CultureInfo.InvariantCulture));
            else if (ch <= 0xFFFF) sb.Append("\\u").Append(ch.ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append("\\U").Append(ch.ToString("x8", CultureInfo.InvariantCulture));
        }
        sb.Append(quote);
    }

    /// <summary>str.isprintable() for one character: not Other or Separator (the space excepted).</summary>
    private static bool IsPrintable(Rune r)
    {
        if (r.Value == ' ') return true;
        return Rune.GetUnicodeCategory(r) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }

    /// <summary>text[:n] — Python slices by code point, not UTF-16 unit.</summary>
    public static string Slice(string text, int n)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (i++ >= n) break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}
