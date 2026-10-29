using System.Net;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>Access requests: review, approve (creates the account), block, unblock, delete.</summary>
public class RequestsTests
{
    private static async Task<int> NewRequestAsync(string? email = null, string name = "Pending Pete", string? notes = null)
    {
        await using var db = S.Db();
        var r = new RegistrationRequest
        {
            FullName = name, Email = email ?? $"req.{S.Tag()}@example.com", Phone = "+92 321 " + Random.Shared.Next(1000000, 9999999),
            PasswordHash = PasswordHasher.Hash("mypassword1"), Status = "pending", Seen = false, Notes = notes,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    private static async Task<RegistrationRequest?> RequestAsync(int id)
    {
        await using var db = S.Db();
        return await db.Set<RegistrationRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    [ReferenceFact]
    public async Task The_console_lists_requests_with_counts_and_the_nav_badge()
    {
        await using var host = await S.HostAsync();
        int id = await NewRequestAsync(name: "Pending Pete", notes: "Interested in the platform");
        var page = await S.Super(host).GetStringAsync("/superadmin/requests/");
        Assert.Contains("Pending Pete", page);
        Assert.Contains("<span class=\"pill pill-warning\">pending</span>", page);
        Assert.Contains("Pending Review", page);
        Assert.Contains("Interested in the platform", page);
        Assert.Contains($"action=\"/superadmin/requests/{id}/approve\"", page);
        Assert.Contains("border-radius:999px;\">", page);                // the badge on Requests
        // As in Python, viewing shows the request as seen but does not store it.
        Assert.False((await RequestAsync(id))!.Seen);
        Assert.DoesNotContain("sa-row-new", page);
    }

    [ReferenceFact]
    public async Task Approving_creates_the_account_with_the_requested_password()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string email = $"approve.{S.Tag()}@example.com";
        int id = await NewRequestAsync(email, "Approve Me");
        var r = await S.PostAsync(c, $"/superadmin/requests/{id}/approve", "/superadmin/requests/");
        Assert.Equal("/superadmin/requests/", S.Location(r));
        Assert.Contains($"Access granted to Approve Me ({email}). They can now sign in.", await S.FollowAsync(c, r));

        await using var db = S.Db();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == email);
        Assert.True(u.IsActive == true && u.HasHrAccess == true && u.HasAccountingAccess == false);
        Assert.True(PasswordHasher.Verify(u.PasswordHash, "mypassword1"));
        Assert.Equal(email, u.LoginId);
        Assert.Matches("^SA\\d{4,}$", u.EmployeeCode);
        var req = (await RequestAsync(id))!;
        Assert.Equal(("approved", true, S.SuperAdmin), (req.Status, req.Seen, req.ReviewedBy));
        Assert.NotNull(req.ReviewedAt);
        Assert.Equal(req.Phone, u.Phone);

        // A second approve says so and makes no second account.
        r = await S.PostAsync(c, $"/superadmin/requests/{id}/approve", "/superadmin/requests/");
        Assert.Contains("This request was already approved.", await S.FollowAsync(c, r));
        Assert.Equal(1, await db.Users.CountAsync(x => x.Email == email));
    }

    [ReferenceFact]
    public async Task An_email_that_already_signs_in_is_not_made_twice()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string email = $"exists.{S.Tag()}@example.com";
        await S.NewUserAsync(email);
        int id = await NewRequestAsync(email.ToUpperInvariant());
        var r = await S.PostAsync(c, $"/superadmin/requests/{id}/approve", "/superadmin/requests/");
        Assert.Contains($"An account already signs in as {email.ToUpperInvariant()} — the request was not turned into a second one.", await S.FollowAsync(c, r));
        Assert.Equal("pending", (await RequestAsync(id))!.Status);
    }

    [ReferenceFact]
    public async Task Block_unblock_delete_and_unknown_actions()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int id = await NewRequestAsync(name: "Block Me");
        var r = await S.PostAsync(c, $"/superadmin/requests/{id}/block", "/superadmin/requests/");
        Assert.Contains("Request from Block Me (", await S.FollowAsync(c, r));
        Assert.Equal(("blocked", true), ((await RequestAsync(id))!.Status, (await RequestAsync(id))!.Seen));
        Assert.Contains($"action=\"/superadmin/requests/{id}/unblock\"", await c.GetStringAsync("/superadmin/requests/"));

        r = await S.PostAsync(c, $"/superadmin/requests/{id}/unblock", "/superadmin/requests/");
        Assert.Contains("Request from Block Me unblocked. Back to review queue.", await S.FollowAsync(c, r));
        Assert.Equal("seen", (await RequestAsync(id))!.Status);

        r = await S.PostAsync(c, $"/superadmin/requests/{id}/explode", "/superadmin/requests/");
        Assert.Contains("Unknown action.", await S.FollowAsync(c, r));
        Assert.Equal("seen", (await RequestAsync(id))!.Status);

        r = await S.PostAsync(c, $"/superadmin/requests/{id}/delete", "/superadmin/requests/");
        Assert.Contains("Request deleted.", await S.FollowAsync(c, r));
        Assert.Null(await RequestAsync(id));
    }

    [ReferenceFact]
    public async Task Six_approvals_at_once_make_one_account()
    {
        await using var host = await S.HostAsync();
        string email = $"race.{S.Tag()}@example.com";
        int id = await NewRequestAsync(email);
        var clients = host.ClientsFor(S.SuperAdmin, null, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync("/superadmin/requests/")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/superadmin/requests/{id}/approve", [], tokens[i]));
        Race.NoServerErrors(rs);
        await using var db = S.Db();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));
        Assert.Equal("approved", (await RequestAsync(id))!.Status);
    }
}
