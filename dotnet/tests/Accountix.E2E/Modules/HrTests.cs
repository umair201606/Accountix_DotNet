using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Modules;

/// <summary>
/// HR in the browser (AGENTS.md's HR list: Dashboard, Attendance, Leave, ESS, Profile;
/// tests/e2e/test_hr_pipeline.py), in company 1 "Default": the employee clocks in, takes a
/// break and clocks out through the page's buttons (JSON posts with the antiforgery
/// header), applies for leave through the form, and the admin decides it on the
/// approvals page. Login, hub and logout are Platform's (Modules/PlatformTests).
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class HrTests(FullApp app) : IClassFixture<FullApp>
{
    private static int Employee => TestSupport.Reference.UserId("emp@solarkon.com");

    private static DateOnly CompanyToday => DateOnly.FromDateTime(Accounting.AccountingSeed.CompanyNow);

    [E2ETheory("Hr")]
    [InlineData("admin")]
    [InlineData("employee")]
    public async Task Hr_dashboard_loads_for_admin_and_employee(string who)
    {
        int user = who == "admin" ? E2EEnv.SuperAdmin : Employee;
        await using var s = await app.SessionAsync(Viewport.Desktop, user, E2EEnv.DefaultCompany, test: "hr_dash_" + who);
        await s.GoAsync("/dashboard");
        await Expect(s.Page.Locator(".content-area")).Not.ToBeEmptyAsync();
        // The HR sidebar: attendance, leave and self-service are a click away.
        foreach (string href in new[] { "/attendance/", "/leaves/", "/ess/" })
            await Expect(s.Page.Locator($"#sidebar a[href='{href}']").First).ToBeAttachedAsync();
        await s.AssertCleanAsync();
    }

    [E2ETheory("Hr")]
    [InlineData("desktop")]
    [InlineData("320")]
    public async Task Employee_clocks_in_takes_a_break_and_clocks_out(string viewport)
    {
        await ResetTodayAsync();
        await using var s = await app.SessionAsync(Viewport.Named(viewport), Employee, E2EEnv.DefaultCompany, test: "attendance");
        var p = s.Page;
        await s.GoAsync("/attendance/");
        await ClickAndReloadAsync(s, "#clock-in-btn");
        await Expect(p.Locator("#clock-out-btn")).ToBeVisibleAsync();
        await ClickAndReloadAsync(s, "#break-start-btn");
        await Expect(p.Locator("#break-end-btn")).ToBeVisibleAsync();
        await ClickAndReloadAsync(s, "#break-end-btn");
        await ClickAndReloadAsync(s, "#clock-out-btn");
        await Expect(p.GetByText("Shift completed for today.")).ToBeVisibleAsync();
        using (var db = Db.For(E2EEnv.DefaultCompany))
        {
            var day = db.Set<Attendance>().Single(a => a.UserId == Employee && a.Date == CompanyToday);
            Assert.NotNull(day.ClockIn);
            Assert.NotNull(day.ClockOut);
            var events = db.Set<AttendanceLog>().Where(l => l.AttendanceId == day.Id).Select(l => l.Event).ToList();
            Assert.Contains("clock_in", events);
            Assert.Contains("clock_out", events);
            var breaks = db.Set<BreakLog>().Where(b => b.AttendanceId == day.Id).ToList();
            Assert.Single(breaks);
            Assert.NotNull(breaks[0].BreakEnd);
        }
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact("Hr")]
    public async Task Leave_is_applied_for_through_the_form_and_decided_on_the_approvals_page()
    {
        // A future weekday no other run has used.
        var start = CompanyToday.AddDays(60 + Random.Shared.Next(0, 900));
        while (start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            start = start.AddDays(1);
        string reason = $"E2E leave {Guid.NewGuid().ToString("N")[..6]}";
        await EnsureLeaveTypesAsync();

        await using (var s = await app.SessionAsync(Viewport.Phone375, Employee, E2EEnv.DefaultCompany, test: "leave_apply"))
        {
            var p = s.Page;
            await s.GoAsync("/leaves/");
            var apply = p.Locator("a:has-text('Apply')").First;
            await Expect(apply).ToBeVisibleAsync();
            await Accounting.VoucherUi.SubmitAndWaitAsync(s, apply, @"/leaves/apply$");
            var type = p.Locator("select[name=leave_type]");
            string value = await type.EvaluateAsync<string>("el => [...el.options].find(o => o.value).value");
            await type.SelectOptionAsync(value);
            await p.Locator("input[name=start_date]").FillAsync(start.ToString("yyyy-MM-dd"));
            await p.Locator("input[name=end_date]").FillAsync(start.ToString("yyyy-MM-dd"));
            await p.Locator("textarea[name=reason]").FillAsync(reason);
            await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Submit Request" }), @"/leaves/");
            await Expect(p.Locator(".app-flash-success, .app-flash-info")).ToBeVisibleAsync();
            await s.AssertCleanAsync();
        }
        LeaveRequest req;
        using (var db = Db.For(E2EEnv.DefaultCompany))
            req = db.Set<LeaveRequest>().AsNoTracking().Single(r => r.Reason == reason);
        Assert.Equal("pending", req.Status);
        Assert.Equal(1m, req.TotalDays);

        // The admin rejects it with a reason (rejection frees the days; the dates stay reusable).
        await using (var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: "leave_decide"))
        {
            var p = s.Page;
            await s.GoAsync("/leaves/approvals");
            var row = p.Locator("tr", new() { HasTextString = reason });
            if (await row.CountAsync() == 0)
                row = p.Locator("tr", new() { HasTextString = start.ToString("dd MMM yyyy") });
            await Expect(row.First).ToBeVisibleAsync();
            await row.First.Locator("input[name=comment]").FillAsync("E2E: not this time");
            await Accounting.VoucherUi.SubmitAndWaitAsync(s, row.First.GetByRole(AriaRole.Button, new() { Name = "Reject" }), @"/leaves/");
            await s.AssertCleanAsync();
        }
        using (var db = Db.For(E2EEnv.DefaultCompany))
            Assert.Equal("rejected", db.Set<LeaveRequest>().Single(r => r.Id == req.Id).Status);
    }

    [E2EFact("Hr")]
    public async Task Ess_shows_the_employees_own_record()
    {
        await using var s = await app.SessionAsync(Viewport.Phone360, Employee, E2EEnv.DefaultCompany);
        await s.GoAsync("/ess/");
        await Expect(s.Page.Locator(".content-area")).ToContainTextAsync("Employee User");
        await s.GoAsync("/ess/loans");
        await s.GoAsync("/ess/slips");
        await s.AssertCleanAsync();
    }

    [E2EFact("Hr")]
    public async Task Profile_and_change_password_pages()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, Employee, E2EEnv.DefaultCompany);
        await s.GoAsync("/auth/profile");
        string body = await s.Page.Locator("body").InnerTextAsync();
        Assert.Contains("profile", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("emp@solarkon.com", body);
        await s.GoAsync("/auth/change-password");
        Assert.Contains("password", s.Page.Url);
        Assert.True(await s.Page.Locator("input[type=password]").CountAsync() >= 2);
        await s.AssertCleanAsync();
    }

    private static async Task ClickAndReloadAsync(UiSession s, string button)
    {
        var p = s.Page;
        var answer = p.WaitForResponseAsync(r => r.Request.Method == "POST" && r.Url.Contains("/attendance/"));
        await p.Locator(button).ClickAsync();
        var r = await answer;
        Assert.True(r.Ok, $"{button}: {r.Status} {await r.TextAsync()}");
        await Expect(p.Locator("#clock-status")).Not.ToBeEmptyAsync();
        // The page reloads itself 1.5 s after a success.
        await p.WaitForLoadStateAsync(LoadState.Load);
        await p.WaitForFunctionAsync("() => !document.getElementById('clock-status') || document.getElementById('clock-status').textContent === ''",
            null, new() { Timeout = 10_000 });
        await s.SettleAsync();
        s.AssertNoProblems("after " + button);
    }

    /// <summary>
    /// Leave types, as hr_app/seed.py writes them. Neither app has a page that creates them
    /// (Python seeds them with that script), and the reference database was built without it.
    /// </summary>
    private static async Task EnsureLeaveTypesAsync()
    {
        await using var db = Db.For(E2EEnv.DefaultCompany);
        if (await db.Set<LeaveType>().AnyAsync())
            return;
        db.AddRange(
            new LeaveType { Name = "Casual Leave", Code = "CL", DefaultQuota = 12, IsPaid = true, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 },
            new LeaveType { Name = "Sick Leave", Code = "SL", DefaultQuota = 10, IsPaid = true, RequiresApproval = true, CarryForward = true, MaxCarryForward = 5 },
            new LeaveType { Name = "Annual Leave", Code = "AL", DefaultQuota = 20, IsPaid = true, RequiresApproval = true, CarryForward = true, MaxCarryForward = 10 });
        await db.SaveChangesAsync();
    }

    /// <summary>The employee's attendance for today, removed so the clock can start again (e2e copy only).</summary>
    private static async Task ResetTodayAsync()
    {
        await using var db = Db.For(E2EEnv.DefaultCompany);
        var ids = await db.Set<Attendance>().Where(a => a.UserId == Employee && a.Date == CompanyToday).Select(a => a.Id).ToListAsync();
        if (ids.Count == 0)
            return;
        await db.Set<AttendanceLog>().Where(l => ids.Contains(l.AttendanceId)).ExecuteDeleteAsync();
        await db.Set<BreakLog>().Where(l => ids.Contains(l.AttendanceId)).ExecuteDeleteAsync();
        await db.Set<Attendance>().Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync();
    }
}
