using System.Net;
using System.Text.RegularExpressions;

namespace Accountix.SuperAdmin.Tests;

/// <summary>
/// Compares a .NET page with the Python page it ports, as markup: entities
/// decoded (Razor writes non-ASCII and quotes as character references, Jinja
/// as UTF-8 / &amp;#39;), whitespace around tags dropped, boolean attributes
/// in one spelling, and the antiforgery fields (.NET only) removed.
/// </summary>
internal static partial class Markup
{
    public static string Normalize(string html)
    {
        html = Antiforgery().Replace(html, "");
        html = WebUtility.HtmlDecode(html);
        html = Ws().Replace(html, " ");
        html = BeforeTag().Replace(html, "<");
        html = AfterTag().Replace(html, ">");
        html = html.Replace(" >", ">");                 // Jinja's `<option value="3" {{ '' }}>`
        foreach (string b in new[] { "checked", "selected", "disabled" })
            html = html.Replace($" {b}=\"{b}\"", $" {b}");
        return html.Trim();
    }

    /// <summary>Every &lt;table class="…"&gt; … &lt;/table&gt; of the page, normalised.</summary>
    public static string Rows(string html, string tableClass)
    {
        var parts = Regex.Matches(html, $"<table class=\"{Regex.Escape(tableClass)}\"[^>]*>.*?</table>", RegexOptions.Singleline)
            .Select(m => Normalize(m.Value));
        return string.Join("\n", parts);
    }

    /// <summary>The page between two markers (markers included), normalised.</summary>
    public static string Between(string html, string start, string end)
    {
        int i = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{start}' not on the page");
        int j = html.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j >= 0, $"'{end}' not after '{start}'");
        return Normalize(html[i..(j + end.Length)]);
    }

    [GeneratedRegex("<input name=\"__RequestVerificationToken\"[^>]*>")]
    private static partial Regex Antiforgery();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();

    [GeneratedRegex(@"\s*<")]
    private static partial Regex BeforeTag();

    [GeneratedRegex(@">\s*")]
    private static partial Regex AfterTag();
}
