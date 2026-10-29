using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.TestSupport;
using Microsoft.Playwright;
using Xunit.Sdk;

namespace Accountix.E2E.Harness;

/// <summary>
/// One person in one browser context at one width, with the suite's guards on
/// every page it opens: any console error, uncaught script error or 5xx answer
/// (page, fetch or asset) is a failure; so is a page wider than the screen
/// (documentElement.scrollWidth &gt; innerWidth) or content cut off past the
/// right edge outside a scroll box (the Python suite's 360px check), and a
/// page served by any server but this run's. Ends with <see cref="AssertCleanAsync"/>;
/// a session disposed without it (a failed test) leaves a screenshot.
/// </summary>
public sealed partial class UiSession : IAsyncDisposable
{
    private readonly List<string> _problems = [];
    private readonly List<string> _dialogs = [];
    private readonly string _test;
    private bool _clean;

    private UiSession(AppServer server, IBrowserContext context, IPage page, Viewport viewport, string test)
    {
        Server = server;
        Context = context;
        Page = page;
        Viewport = viewport;
        _test = test;
    }

    public AppServer Server { get; }
    public IBrowserContext Context { get; }
    public IPage Page { get; }
    public Viewport Viewport { get; }

    /// <summary>Accept browser confirm()/alert() dialogs (default: dismiss, as Playwright does). Each is recorded.</summary>
    public bool AcceptDialogs { get; set; }

    /// <summary>The dialogs the page raised (type: message).</summary>
    public IReadOnlyList<string> Dialogs => _dialogs;

    /// <summary>Console errors, script errors and 5xx answers seen so far (not yet asserted).</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Console errors a test expects (e.g. a refused fetch it fires itself): matched by substring.</summary>
    public List<string> ExpectedConsoleErrors { get; } = [];

    /// <summary>
    /// Opens a context signed in as <paramref name="userId"/> in <paramref name="companyId"/>
    /// (a Python-format session cookie plus its user_sessions row, as TestSignIn does),
    /// or anonymous when <paramref name="userId"/> is null.
    /// </summary>
    public static async Task<UiSession> OpenAsync(AppServer server, Viewport viewport, int? userId, int? companyId = null,
        ColorScheme colorScheme = ColorScheme.Light, [CallerMemberName] string test = "")
    {
        var browser = await Browsers.SharedAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = server.BaseUrl,
            ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height },
            ColorScheme = colorScheme,
            Locale = "en-US",
        });
        context.SetDefaultTimeout(15_000);
        context.SetDefaultNavigationTimeout(30_000);
        // Web fonts and icon CSS from CDNs: answered empty, so runs don't depend on the
        // network (and timings measure the app). Anything else external goes through.
        await context.RouteAsync(url => IsCdnStyle(url), route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = route.Request.Url.Contains(".css") || route.Request.Url.Contains("/css") ? "text/css" : "font/woff2",
            Body = "",
        }));
        if (userId is int uid)
        {
            string cookie = TestSignIn.CookieFor(uid, companyId, server.ConnectionString);
            await context.AddCookiesAsync([new Cookie
            {
                Name = TestSignIn.CookieName, Value = cookie, Domain = "127.0.0.1", Path = "/", HttpOnly = true, SameSite = SameSiteAttribute.Lax,
            }]);
        }
        var page = await context.NewPageAsync();
        var session = new UiSession(server, context, page, viewport, test);
        session.Watch(page);
        context.Page += (_, popup) => session.Watch(popup);
        return session;
    }

    private static bool IsCdnStyle(string url) =>
        url.StartsWith("https://fonts.googleapis.com/", StringComparison.Ordinal)
        || url.StartsWith("https://fonts.gstatic.com/", StringComparison.Ordinal)
        || url.StartsWith("https://cdnjs.cloudflare.com/ajax/libs/font-awesome/", StringComparison.Ordinal);

    private bool Ours(string url) => url.StartsWith(Server.BaseUrl, StringComparison.Ordinal);

    private void Watch(IPage page)
    {
        page.Console += (_, m) =>
        {
            if (m.Type != "error")
                return;
            string where = m.Location ?? "";
            // A resource outside this app (a CDN) failing to load is not the app's error.
            if (where.StartsWith("http", StringComparison.Ordinal) && !Ours(where) && m.Text.StartsWith("Failed to load resource"))
                return;
            if (ExpectedConsoleErrors.Any(e => m.Text.Contains(e, StringComparison.Ordinal)))
                return;
            // A call to a module this run does not serve (the Python app answers it in production).
            if (m.Text.Contains("status of 404") && Ours(where) && RouteOwners.ServedElsewhere(PathOf(where), Server.Modules))
            {
                lock (_notes)
                    _notes.Add($"{Short(page.Url)} calls {PathOf(where)} (module {RouteOwners.OwnerOf(PathOf(where))}, not in this run): 404 here");
                return;
            }
            lock (_problems)
                _problems.Add($"console error on {Short(page.Url)}: {m.Text} @ {Short(where)}");
        };
        page.PageError += (_, e) =>
        {
            lock (_problems)
                _problems.Add($"script error on {Short(page.Url)}: {e.Split('\n')[0]}");
        };
        page.Response += (_, r) =>
        {
            if (!Ours(r.Url))
                return;
            if (r.Status >= 500)
                lock (_problems)
                    _problems.Add($"{r.Status} from {r.Request.Method} {Short(r.Url)} ({r.Request.ResourceType})");
            // A form the page submitted and the server refused (400 is the antiforgery check).
            else if (r.Status >= 400 && r.Request.ResourceType == "document" && r.Request.Method == "POST")
                lock (_problems)
                    _problems.Add($"{r.Status} from form POST {Short(r.Url)}" + (r.Status == 400 ? " (antiforgery token missing?)" : ""));
            if (r.Request.ResourceType == "document" && r.Status < 300
                && (!r.Headers.TryGetValue(AppServer.InstanceHeader.ToLowerInvariant(), out var inst) || inst != Server.Instance))
                lock (_problems)
                    _problems.Add($"{Short(r.Url)} was not served by this run's server (instance header '{inst}')");
        };
        page.Dialog += async (_, d) =>
        {
            lock (_dialogs)
                _dialogs.Add($"{d.Type}: {d.Message}");
            if (AcceptDialogs)
                await d.AcceptAsync();
            else
                await d.DismissAsync();
        };
    }

    private string Short(string url) => url.Replace(Server.BaseUrl, "");

    /// <summary>"http://127.0.0.1:5315/reports/dashboard:0:0" → "/reports/dashboard".</summary>
    private string PathOf(string location)
    {
        string rest = Short(location);
        var m = Regex.Match(rest, @"^([^?#]*?)(:\d+:\d+)?([?#].*)?$");
        return m.Success ? m.Groups[1].Value : rest;
    }

    private readonly List<string> _notes = [];

    /// <summary>Things worth knowing that are not failures (calls to modules served by Python in production).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>
    /// Opens <paramref name="path"/> and checks it: the status (default: below 400), no
    /// console/script/server errors, and the layout fits the width.
    /// </summary>
    public async Task<IResponse> GoAsync(string path, int? expectStatus = null, bool checkLayout = true, bool networkIdle = true)
    {
        // A page expected to refuse (403, 404) logs its own status as a console error.
        string? expected = expectStatus is >= 400 and int code ? $"status of {code}" : null;
        if (expected is not null)
            ExpectedConsoleErrors.Add(expected);
        var r = await Page.GotoAsync(path) ?? throw new XunitException($"No response for {path}");
        if (expectStatus is int want)
            Assert.True(r.Status == want, $"{path}: expected {want}, got {r.Status}\n{await BodyStartAsync(r)}");
        else
            Assert.True(r.Status < 400, $"{path}: {r.Status}\n{await BodyStartAsync(r)}");
        if (networkIdle)
            await SettleAsync();
        AssertNoProblems(path);
        if (expected is not null)
            ExpectedConsoleErrors.Remove(expected);
        if (checkLayout)
            await AssertFitsAsync(path);
        return r;
    }

    private static async Task<string> BodyStartAsync(IResponse r)
    {
        try
        {
            string body = await r.TextAsync();
            body = Regex.Replace(body, @"<style[\s\S]*?</style>|<script[\s\S]*?</script>", "");
            body = Regex.Replace(body, @"<[^>]+>", " ");
            body = Regex.Replace(body, @"\s+", " ").Trim();
            return body[..Math.Min(body.Length, 1200)];
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Waits for the page's own fetches to finish (network idle), without failing a page that polls.</summary>
    public async Task SettleAsync(int timeoutMs = 10_000)
    {
        try
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Long-polling or a stream: the load event already fired, carry on.
        }
    }

    /// <summary>Fails with every console/script/server error recorded so far (and clears them).</summary>
    public void AssertNoProblems(string? where = null)
    {
        List<string> all;
        lock (_problems)
        {
            all = [.. _problems];
            _problems.Clear();
        }
        all.AddRange(Server.DrainErrors().Select(e => "server: " + e));
        if (all.Count > 0)
            throw new XunitException($"{(where is null ? "" : where + ": ")}{all.Count} problem(s) at {Viewport}:\n  " + string.Join("\n  ", all));
    }

    /// <summary>
    /// The page fits the screen: nothing scrolls the document sideways, and no element
    /// reaches past the right edge unless it sits in a scroll box or a fixed overlay
    /// (the shell sets body{overflow-x:hidden} on phones, so a too-wide page is cut
    /// off rather than scrollable; the scrollWidth check alone would not see it).
    /// </summary>
    public async Task AssertFitsAsync(string? where = null)
    {
        var fit = await Page.EvaluateAsync<JsonElement>(FitJs);
        int scrollWidth = fit.GetProperty("scrollWidth").GetInt32(), inner = fit.GetProperty("innerWidth").GetInt32();
        var clipped = fit.GetProperty("clipped").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (scrollWidth > inner || clipped.Count > 0)
        {
            string shot = await ScreenshotAsync("overflow");
            throw new XunitException(
                $"{where ?? Short(Page.Url)} at {Viewport}: " +
                (scrollWidth > inner ? $"document is {scrollWidth}px wide on a {inner}px screen. " : "") +
                (clipped.Count > 0 ? "Cut off past the right edge: " + string.Join("; ", clipped) : "") +
                $"\n  screenshot: {shot}");
        }
    }

    private const string FitJs = """
        () => {
          const W = innerWidth, out = [];
          for (const el of document.querySelectorAll('body *')) {
            const r = el.getBoundingClientRect();
            if (r.right <= W + 2 || !r.width || !r.height) continue;
            const cs = getComputedStyle(el);
            if (cs.visibility === 'hidden' || cs.position === 'fixed') continue;
            let p = el.parentElement, contained = false;
            while (p && p !== document.body) {
              const ps = getComputedStyle(p);
              if (ps.overflowX === 'auto' || ps.overflowX === 'scroll' || ps.position === 'fixed') { contained = true; break; }
              p = p.parentElement;
            }
            if (contained) continue;
            // Report the outermost offender only (its children would repeat it).
            let q = el.parentElement, parentOut = false;
            while (q && q !== document.body) { if (q.getBoundingClientRect().right > W + 2) { parentOut = true; break; } q = q.parentElement; }
            if (parentOut) continue;
            out.push(el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + (el.classList.length ? '.' + [...el.classList].join('.') : '') + ' right=' + Math.round(r.right));
          }
          return { scrollWidth: document.documentElement.scrollWidth, innerWidth: W, clipped: out.slice(0, 6) };
        }
        """;

    /// <summary>Saves a full-page screenshot under <see cref="E2EEnv.Screenshots"/>; returns its path.</summary>
    public async Task<string> ScreenshotAsync(string tag)
    {
        string name = Regex.Replace($"{_test}_{Viewport.Name}_{tag}_{DateTime.Now:HHmmss}", @"[^\w.-]+", "_");
        string path = Path.Combine(E2EEnv.Screenshots, name + ".png");
        try
        {
            await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        }
        catch (PlaywrightException)
        {
            return "(no screenshot: page closed)";
        }
        return path;
    }

    /// <summary>The end of every test: no problem left unreported.</summary>
    public async Task AssertCleanAsync()
    {
        await SettleAsync(3_000);
        AssertNoProblems("end of test");
        lock (_notes)
            foreach (string n in _notes.Distinct())
                Console.WriteLine("[note] " + n);
        _clean = true;
    }

    /// <summary>The antiforgery token the current page carries (meta csrf-token).</summary>
    public Task<string> CsrfTokenAsync() =>
        Page.EvaluateAsync<string>("() => document.querySelector('meta[name=\"csrf-token\"]').content");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_clean)
                await ScreenshotAsync("failed");
        }
        finally
        {
            await Context.CloseAsync();
        }
    }
}
