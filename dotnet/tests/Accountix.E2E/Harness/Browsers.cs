using Microsoft.Playwright;

namespace Accountix.E2E.Harness;

/// <summary>
/// One Chromium for the whole run. Tried in order:
/// 1. E2E_BROWSER_CHANNEL (e.g. "chrome") when set;
/// 2. Playwright's own Chromium for this package version (1.64.1 → chromium-1248), from
///    PLAYWRIGHT_BROWSERS_PATH or ~/Library/Caches/ms-playwright — the Python suite
///    (playwright 1.64.1) installed exactly that revision;
/// 3. the installed Google Chrome (Channel = "chrome").
/// Set E2E_HEADED=1 to watch.
/// </summary>
public static class Browsers
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    /// <summary>Which browser launched (for E2E.md and failure messages).</summary>
    public static string? Description { get; private set; }

    /// <summary>Null when a browser launches here, else why not (the skip reason). Probed once.</summary>
    public static string? Unavailable => Probe.Value;

    private static readonly Lazy<string?> Probe = new(() =>
    {
        try
        {
            SharedAsync().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception e)
        {
            return "No browser for Playwright: " + e.Message.Split('\n')[0] +
                   " — install one with `pwsh bin/.../playwright.ps1 install chromium` or set E2E_BROWSER_CHANNEL=chrome (docs/E2E.md).";
        }
    });

    public static async Task<IBrowser> SharedAsync()
    {
        if (_browser is { IsConnected: true })
            return _browser;
        await Gate.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true })
                return _browser;
            _playwright ??= await Playwright.CreateAsync();
            bool headed = Environment.GetEnvironmentVariable("E2E_HEADED") == "1";
            var errors = new List<string>();
            var channels = new List<string?>();
            if (Environment.GetEnvironmentVariable("E2E_BROWSER_CHANNEL") is { Length: > 0 } ch)
                channels.Add(ch);
            channels.Add(null);
            channels.Add("chrome");
            foreach (string? channel in channels.Distinct())
            {
                try
                {
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = !headed,
                        Channel = channel,
                        Timeout = 60_000,
                    });
                    Description = $"{(channel is null ? "Playwright Chromium" : "channel " + channel)} {_browser.Version}";
                    return _browser;
                }
                catch (PlaywrightException e)
                {
                    errors.Add($"{channel ?? "bundled"}: {e.Message.Split('\n')[0]}");
                }
            }
            throw new InvalidOperationException(string.Join(" | ", errors));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Closes the browser and Playwright's driver (end of the run).</summary>
    public static async Task ShutdownAsync()
    {
        if (_browser is not null)
            await _browser.CloseAsync();
        _browser = null;
        _playwright?.Dispose();
        _playwright = null;
    }
}

/// <summary>Holds the shared browser for the collection and closes it at the end.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Browsers.ShutdownAsync();
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<BrowserFixture>
{
    public const string Name = "E2E";
}
