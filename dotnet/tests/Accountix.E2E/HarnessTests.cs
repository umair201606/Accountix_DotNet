using Accountix.E2E.Harness;

namespace Accountix.E2E;

/// <summary>The harness itself: it owns its port, it notices a dead or foreign server, its guards fire.</summary>
[Collection(E2ECollection.Name)]
public sealed class HarnessTests(AccountingApp app) : IClassFixture<AccountingApp>
{
    [E2EFact]
    public async Task A_second_server_on_the_same_port_is_refused()
    {
        var server = await app.ServerAsync();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => AppServer.StartAsync(server.ConnectionString, server.Port, "Accounting"));
        Assert.Contains($"Port {server.Port}", e.Message);
        await server.AssertAliveAsync();
    }

    [E2EFact]
    public async Task Pages_come_from_this_run_and_the_guards_catch_script_errors_and_overflow()
    {
        await using var s = await app.SessionAsync(Viewport.Phone320, E2EEnv.OwnerB, E2EEnv.CompanyB);
        var r = await s.GoAsync("/accounting/");
        Assert.Equal(s.Server.Instance, await r.HeaderValueAsync(AppServer.InstanceHeader));

        // A console error is caught...
        await s.Page.EvaluateAsync("() => console.error('boom from the test')");
        var e = Assert.Throws<Xunit.Sdk.XunitException>(() => s.AssertNoProblems());
        Assert.Contains("boom from the test", e.Message);
        // ...and so is an uncaught script error.
        await s.Page.EvaluateAsync("() => setTimeout(() => { throw new Error('uncaught from the test'); }, 0)");
        await s.Page.WaitForTimeoutAsync(200);
        e = Assert.Throws<Xunit.Sdk.XunitException>(() => s.AssertNoProblems());
        Assert.Contains("uncaught from the test", e.Message);
        // ...and an element wider than the phone.
        await s.Page.EvaluateAsync("() => { const d = document.createElement('div'); d.id = 'tooWide'; d.style.width = '900px'; d.style.height = '10px'; document.querySelector('.content-area').appendChild(d); }");
        var o = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => s.AssertFitsAsync());
        Assert.Contains("tooWide", o.Message);
        await s.Page.EvaluateAsync("() => document.getElementById('tooWide').remove()");
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }
}
