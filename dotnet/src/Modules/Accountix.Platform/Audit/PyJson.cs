using System.Globalization;
using System.Text;

namespace Accountix.Platform.Audit;

/// <summary>
/// The text Python's <c>json.dumps(value, default=str)</c> writes (separators
/// ", " and ": ", ensure_ascii, floats as repr), so an audit row written by
/// .NET reads exactly like one written by the Python app.
/// Values: null, bool, int/long, double (a Python float), string,
/// IEnumerable of values (a list) and <see cref="PyDict"/> (a dict, insertion order).
/// </summary>
public static class PyJson
{
    public static string Dumps(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int or long or short or byte:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(FloatRepr(d, json: true));
                break;
            case float f:
                sb.Append(FloatRepr(f, json: true));
                break;
            case decimal m:
                sb.Append(FloatRepr((double)m, json: true));
                break;
            case string s:
                WriteString(sb, s);
                break;
            case PyDict dict:
            {
                sb.Append('{');
                bool first = true;
                foreach (var (k, val) in dict)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    WriteString(sb, k);
                    sb.Append(": ");
                    Write(sb, val);
                }
                sb.Append('}');
                break;
            }
            case System.Collections.IEnumerable list:
            {
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
            }
            default:
                WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
                break;
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
                    if (c < 0x20 || c > 0x7e)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Python's repr(float): shortest round-trip digits, ".0" on whole numbers, exponent outside 1e-4 .. 1e16.</summary>
    public static string FloatRepr(double d, bool json = false) => Accountix.Core.Shared.PyCompat.FloatRepr(d, json);

}

/// <summary>A Python dict: keys in insertion order.</summary>
public sealed class PyDict : List<KeyValuePair<string, object?>>
{
    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));

    public new IEnumerator<(string Key, object? Value)> GetEnumerator()
    {
        foreach (var kv in (List<KeyValuePair<string, object?>>)this)
            yield return (kv.Key, kv.Value);
    }
}
