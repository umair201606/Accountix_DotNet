using System.Collections;
using System.Globalization;
using System.Text;

namespace Accountix.HrRecords.Services;

/// <summary>
/// Flask's jsonify, byte for byte: keys sorted, no spaces, non-ASCII as
/// \uXXXX (ensure_ascii), floats as repr, a trailing newline, served as
/// application/json. The pages' scripts (and the bell on every page) read
/// these answers; both apps answer them while the move is under way.
/// Values: dictionaries (string keys), lists, strings, bools, ints, doubles,
/// decimals (Flask writes a Decimal as its str), nulls.
/// </summary>
public static class PyJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        sb.Append('\n');
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
            case string s:
                Str(sb, s);
                break;
            case int or long or short:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(Py.Float(d));
                break;
            case float f:
                sb.Append(Py.Float(f));
                break;
            case decimal m:
                Str(sb, m.ToString(CultureInfo.InvariantCulture));
                break;
            case IDictionary dict:
            {
                var keys = dict.Keys.Cast<object>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)!).ToList();
                keys.Sort(StringComparer.Ordinal);
                sb.Append('{');
                bool first = true;
                foreach (string k in keys)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Str(sb, k);
                    sb.Append(':');
                    Write(sb, dict[k]);
                }
                sb.Append('}');
                break;
            }
            case IEnumerable list:
            {
                sb.Append('[');
                bool first = true;
                foreach (object? item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                break;
            }
            default:
                throw new InvalidOperationException($"PyJson cannot write {v.GetType().Name}");
        }
    }

    /// <summary>json.dumps string escaping with ensure_ascii (lower-case hex, surrogate pairs as two escapes).</summary>
    private static void Str(StringBuilder sb, string s)
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
                    if (c < 0x20 || c > 0x7E)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>A jsonify answer: <see cref="PyJson"/> bytes with the status Python returns.</summary>
public sealed class PyJsonResult(object? value, int status = 200) : IActionResult
{
    public object? Value { get; } = value;
    public int Status { get; } = status;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = Status;
        response.ContentType = "application/json";
        byte[] body = Encoding.ASCII.GetBytes(PyJson.Serialize(Value));
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, context.HttpContext.RequestAborted);
    }
}
