using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Accountix.TestSupport;
using Accountix.Web.Hosting;
using Accountix.Web.Infrastructure;

namespace Accountix.Tests;

/// <summary>
/// /static (the CSS and JS every page links) comes from the .NET tree:
/// src/Accountix.Web.Shared/static, copied next to every build and publish.
/// Nothing is read from the Python app's folder any more.
/// </summary>
public partial class StaticFilesTests
{
    private static string DotnetRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));

    private static string Source => Path.Combine(DotnetRoot(), "src", "Accountix.Web.Shared", "static");

    /// <summary>Every "/static/..." a page, partial, controller or script of the .NET app names.</summary>
    private static SortedSet<string> Referenced()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(DotnetRoot(), "src"), "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || Path.GetExtension(f) is not (".cshtml" or ".cs" or ".js" or ".css"))
                continue;
            foreach (Match m in StaticUrl().Matches(File.ReadAllText(f)))
                found.Add(m.Groups[1].Value);
        }
        return found;
    }

    [GeneratedRegex(@"/static/([A-Za-z0-9_./-]+\.[a-z0-9]+)")]
    private static partial Regex StaticUrl();

    [Fact]
    public void Every_static_url_the_app_names_is_in_the_dotnet_tree_and_nothing_else_is()
    {
        var referenced = Referenced();
        Assert.Contains("css/app.css", referenced);
        Assert.Contains("js/doc_form.js", referenced);
        var present = Directory.EnumerateFiles(Source, "*.*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Source, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal("", string.Join(", ", referenced.Where(r => !present.Contains(r))));   // a page links a file that does not ship
        Assert.Equal("", string.Join(", ", present.Where(p => !referenced.Contains(p))));   // a file nothing links
    }

    [Fact]
    public void The_build_copies_static_next_to_the_binaries()
    {
        string? folder = AccountixApp.StaticFolder();
        Assert.NotNull(folder);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "static"), folder);
        foreach (string f in Directory.EnumerateFiles(Source, "*.*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(folder!, Path.GetRelativePath(Source, f))));
    }

    [ReferenceFact]
    public async Task Static_files_answer_200_from_the_dotnet_tree()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountixHost).Assembly);
        var c = host.AnonymousClient();
        foreach (string rel in Referenced())
        {
            var r = await c.GetAsync("/static/" + rel);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"/static/{rel}: {(int)r.StatusCode}");
            Assert.Equal(rel.EndsWith(".css") ? "text/css" : "text/javascript", r.Content.Headers.ContentType?.MediaType);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Source, rel)), await r.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/static/css/nope.css")).StatusCode);
    }
}
