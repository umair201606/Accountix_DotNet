using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accountix.SuperAdmin.Infrastructure;

/// <summary>The bits of Python the console's routes and templates lean on.</summary>
public static partial class Py
{
    /// <summary>shared/routes/portal.py SLUG_RE.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,60}$")]
    public static partial Regex SlugRe();

    /// <summary>The value of a form field as request.form.get(name, "").</summary>
    public static string Form(IFormCollection form, string name) =>
        form.TryGetValue(name, out var v) && v.Count > 0 ? v[0] ?? "" : "";

    /// <summary>request.form.get(name) is present (not None).</summary>
    public static bool Has(IFormCollection form, string name) => form.TryGetValue(name, out var v) && v.Count > 0;

    /// <summary>str.strip().</summary>
    public static string Strip(string? s) => (s ?? "").Trim();

    /// <summary>
    /// int(text) as Python reads a form value: surrounding whitespace, an
    /// optional sign, ASCII digits with single underscores between them.
    /// False for anything else (Python raises ValueError).
    /// </summary>
    public static bool TryInt(string? text, out int value)
    {
        value = 0;
        string s = Strip(text);
        if (s.Length == 0) return false;
        int i = 0;
        bool neg = false;
        if (s[0] is '+' or '-') { neg = s[0] == '-'; i = 1; }
        if (i >= s.Length || s[i] == '_' || s[^1] == '_') return false;
        var digits = new StringBuilder();
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= '0' and <= '9') digits.Append(c);
            else if (c == '_' && s[i - 1] != '_' && char.IsAsciiDigit(s[i - 1])) continue;
            else return false;
        }
        if (!long.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long l)) return false;
        l = neg ? -l : l;
        if (l is < int.MinValue or > int.MaxValue) return false;
        value = (int)l;
        return true;
    }

    /// <summary>urllib.parse.quote_plus(s, safe=''): what urlencode writes for a query value.</summary>
    public static string QuotePlus(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>urlencode(dict): key=value pairs in the dict's order.</summary>
    public static string UrlEncode(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => QuotePlus(p.Key) + "=" + QuotePlus(p.Value)));

    /// <summary>The first word of s.split(sep).</summary>
    public static string Before(string s, char sep) => s.Split(sep)[0];

    /// <summary>str(value) of a value json.loads produced, as Jinja prints it.</summary>
    public static string Str(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        _ => Repr(v),
    };

    /// <summary>repr() of a json.loads value (strings quoted, as inside a list or dict).</summary>
    public static string Repr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "None",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.String => StrRepr(v.GetString() ?? ""),
        JsonValueKind.Number => NumberRepr(v),
        JsonValueKind.Array => "[" + string.Join(", ", v.EnumerateArray().Select(Repr)) + "]",
        JsonValueKind.Object => "{" + string.Join(", ", v.EnumerateObject().Select(p => StrRepr(p.Name) + ": " + Repr(p.Value))) + "}",
        _ => v.GetRawText(),
    };

    private static string NumberRepr(JsonElement v)
    {
        string raw = v.GetRawText();
        // json.loads: a number with a fraction or exponent is a float, else an int.
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
            return raw.TrimStart('+');
        return Accountix.Platform.Audit.PyJson.FloatRepr(v.GetDouble());
    }

    /// <summary>
    /// Jinja's <c>value|tojson|forceescape</c> for a string, as HTML to write
    /// raw into an attribute: json.dumps (ASCII only), then &lt; &gt; &amp; '
    /// as \u escapes (htmlsafe_json_dumps), then the double quotes as &amp;#34;.
    /// </summary>
    public static string TojsonForceescape(string? s)
    {
        var sb = new StringBuilder("&#34;");
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\&#34;"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                case '\'': sb.Append("\\u0027"); break;
                default:
                    if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append("&#34;").ToString();
    }

    /// <summary>repr(str): single quotes unless the text holds a single quote and no double one.</summary>
    public static string StrRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(q);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == q) sb.Append('\\').Append(c);
                    else if (c < ' ' || c == '\x7f') sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append(q).ToString();
    }
}
