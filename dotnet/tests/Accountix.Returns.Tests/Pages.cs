using System.Text.RegularExpressions;

namespace Accountix.Returns.Tests;

public static partial class Pages
{
    [GeneratedRegex("<script(?:\\s[^>]*)?>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex Scripts();

    /// <summary>Inline scripts must reach the browser as JavaScript, never HTML-encoded (&amp;quot; is a syntax error).</summary>
    public static void AssertScriptsAreRaw(string html)
    {
        foreach (Match m in Scripts().Matches(html))
        {
            string js = m.Groups[1].Value;
            Assert.DoesNotContain("&quot;", js);
            Assert.DoesNotContain("&amp;", js);
            Assert.DoesNotContain("&#x27;", js);
            Assert.DoesNotContain("&#39;", js);
        }
    }
}
