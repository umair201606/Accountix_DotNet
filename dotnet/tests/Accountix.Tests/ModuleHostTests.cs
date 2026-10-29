using System.Net;
using Accountix.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Tests;

/// <summary>The per-module test host and test sign-in every module's tests use.</summary>
public class ModuleHostTests
{
    [ReferenceFact]
    public async Task A_module_host_signs_people_in_like_the_python_app()
    {
        await using var host = await ModuleHost.StartAsync(typeof(Accountix.Web.Controllers.AccessController).Assembly);
        int owner = Reference.UserId(Reference.Email("owner_b"));
        var r = await host.ClientFor(owner, Reference.Company("b")).GetAsync("/net/access");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Role here</dt><dd>admin", await r.Content.ReadAsStringAsync());

        var anon = await host.AnonymousClient().GetAsync("/net/access");
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
    }
}

/// <summary>The real host registers every module's services (not their stand-ins).</summary>
public class HostRegistrationTests
{
    [ReferenceFact]
    public void The_host_uses_the_modules_real_services()
    {
        using var app = new ReferenceApp<Program>();
        using var scope = app.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<Accountix.Core.Shared.ILedger>();
        Assert.Equal("Accountix.Accounting.Services.Ledger", ledger.GetType().FullName);
    }
}
