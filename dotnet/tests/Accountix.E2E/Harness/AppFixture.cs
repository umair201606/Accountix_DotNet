using Accountix.TestSupport;
using Microsoft.Playwright;

namespace Accountix.E2E.Harness;

/// <summary>
/// A test class's server (xUnit class fixture). Started on first use, so a class whose
/// tests all skip never boots anything; stopped when the class is done. Each test
/// first checks the server is still the one this run started.
/// </summary>
public abstract class AppFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppServer? _server;

    /// <summary>Module projects booted with Core + Web.Shared (Accountix.&lt;Name&gt;).</summary>
    protected abstract string[] Modules { get; }

    protected virtual int Port => E2EEnv.AppPort;

    protected virtual string ConnectionString => E2EEnv.ConnectionString;

    public async Task<AppServer> ServerAsync()
    {
        if (_server is not null)
        {
            await _server.AssertAliveAsync();
            return _server;
        }
        await _gate.WaitAsync();
        try
        {
            _server ??= await AppServer.StartAsync(ConnectionString, Port, Modules);
            return _server;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A signed-in browser session on this fixture's server.</summary>
    public async Task<UiSession> SessionAsync(Viewport viewport, int? userId, int? companyId = null,
        ColorScheme colorScheme = ColorScheme.Light, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => await UiSession.OpenAsync(await ServerAsync(), viewport, userId, companyId, colorScheme, test);

    /// <summary>An HTTP client signed in on this server (data set-up; the browser does the testing).</summary>
    public async Task<PageClient> HttpAsync(int userId, int companyId)
    {
        var server = await ServerAsync();
        var c = new PageClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(server.BaseUrl + "/"),
        };
        c.Cookies.Add(new Uri(server.BaseUrl + "/"), new System.Net.Cookie(TestSignIn.CookieName, TestSignIn.CookieFor(userId, companyId, server.ConnectionString)));
        return c;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        _server = null;
    }
}

/// <summary>Accounting alone (it needs nothing else), on the e2e copy.</summary>
public sealed class AccountingApp : AppFixture
{
    protected override string[] Modules => ["Accounting"];
}

/// <summary>
/// The integrated app: every module in this build (E2EModules), as the real host
/// loads them. Module tests use it; each test still names the modules it needs.
/// </summary>
public sealed class FullApp : AppFixture
{
    protected override string[] Modules => ModuleStatus.InBuild.Where(ModuleStatus.AssemblyPresent).ToArray();
    protected override int Port => 5317;
}

/// <summary>The integrated app on the full-year speed-test database (read-only).</summary>
public sealed class FullPerfApp : AppFixture
{
    protected override string[] Modules => ModuleStatus.InBuild.Where(ModuleStatus.AssemblyPresent).ToArray();
    protected override int Port => 5318;
    protected override string ConnectionString => Perf.ConnectionString;
}

/// <summary>Accounting on the full-year speed-test database (read-only).</summary>
public sealed class AccountingPerfApp : AppFixture
{
    protected override string[] Modules => ["Accounting"];
    protected override int Port => E2EEnv.PerfPort;
    protected override string ConnectionString => Perf.ConnectionString;
}
