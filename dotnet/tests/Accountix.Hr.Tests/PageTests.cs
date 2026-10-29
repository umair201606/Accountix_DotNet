using System.Net;
using System.Net.Http.Headers;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// Every HR page renders for the people it is for (with Python's ids and
/// classes, the HR sidebar and the back link), refuses the rest as Python
/// does, and lists cost the same few queries whatever their length.
/// </summary>
public class PageTests
{
    private static async Task<World> PagesWorldAsync()
    {
        var w = await World.CreateAsync("Pages Co", ("admin", "Pia Admin", Role.Admin), ("mgr", "Pam Manager", Role.Manager),
                                         ("emp", "Pete Employee", Role.Employee));
        await w.SetManagerAsync("emp", "mgr");
        w.Ids["lt"] = await w.LeaveTypeAsync("Casual", "CL", 8);
        await using var db = w.Db();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")));
        var now = DateTime.UtcNow;
        db.Add(new Attendance { UserId = w["emp"], Date = today, ClockIn = today.ToDateTime(new TimeOnly(9, 40)), Status = "present", IsLate = true, IsHalfDay = false, CreatedAt = now });
        db.Add(new Attendance { UserId = w["emp"], Date = today.AddDays(-1), ClockIn = today.AddDays(-1).ToDateTime(new TimeOnly(9, 0)), ClockOut = today.AddDays(-1).ToDateTime(new TimeOnly(19, 0)), Status = "present", IsLate = false, IsHalfDay = false, CreatedAt = now });
        db.Add(new OvertimeAccount { UserId = w["emp"], Date = today.AddDays(-1), RegularHours = 8, OvertimeHours = 2, DoubleOvertime = 0, Approved = false, CreatedAt = now });
        var lr = new LeaveRequest { UserId = w["emp"], LeaveTypeId = w["lt"], StartDate = today.AddDays(10), EndDate = today.AddDays(11), TotalDays = 2, Reason = "wedding", Status = "pending", IsHalfDay = false, CreatedAt = now, UpdatedAt = now };
        db.Add(lr);
        db.Add(new CompanyHoliday { Name = "Founders Day", HolidayDate = today.AddDays(20), IsRecurring = true, Department = "", IsActive = true, CreatedAt = now });
        db.Add(new TimePolicy { Name = "Day shift", Department = "", ShiftStart = new TimeOnly(9, 0), ShiftEnd = new TimeOnly(18, 0), GracePeriodMinutes = 15, MaxRegularHours = 8, MaxOvertimeHours = 4, MaxConsecutiveDays = 6, RequireBreak = true, MinBreakMinutes = 30, IsActive = true, CreatedAt = now });
        db.Add(new ApprovalWorkflow { Name = "Casual flow", Module = "leaves", LeaveTypeId = w["lt"], ApprovalLevels = 1, AutoApprove = false, NotifyAdmins = true, RequiresApproval = true, IsActive = true, CreatedAt = now });
        db.Add(new Project { Name = "Solar farm", Code = "SF-" + w.Tag, Status = "active", IsBillable = true, CreatedAt = now });
        db.Add(new LoanAdvanceRequest { UserId = w["emp"], RequestType = "advance", Amount = 15000, Purpose = "rent", InstallmentMonths = 3, MonthlyInstallment = 5000, RemainingAmount = 15000, Status = "pending", QueueForPayroll = false, CreatedAt = now });
        db.Add(new ChangeRequest { UserId = w["emp"], FieldName = "phone", OldValue = "", NewValue = "0300-1234567", Status = "pending", CreatedAt = now });
        await db.SaveChangesAsync();
        db.Add(new LeaveApproval { LeaveRequestId = lr.Id, ApproverId = w["mgr"], Level = 1, Status = "pending", CreatedAt = now });
        await db.SaveChangesAsync();
        return w;
    }

    /// <summary>(url, who may open it, a marker of Python's markup).</summary>
    private static readonly (string Url, string[] Open, string Marker)[] Pages =
    [
        ("/dashboard", ["admin", "mgr", "emp"], "id=\"dashboard-stats\""),
        ("/attendance/", ["admin", "mgr", "emp"], "id=\"clock-status\""),
        ("/attendance/overview?period=weekly", ["admin", "mgr", "emp"], "Overtime Account"),
        ("/attendance/admin", ["admin"], "class=\"btn btn-sm btn-outline correct-btn\""),
        ("/attendance/policies", ["admin"], "id=\"policy-form\""),
        ("/leaves/", ["admin", "mgr", "emp"], "My Leave Requests"),
        ("/leaves/apply", ["admin", "mgr", "emp"], "name=\"is_half_day\""),
        ("/leaves/approvals", ["admin", "mgr"], "Leave Approval Requests"),
        ("/leaves/holidays", ["admin"], "id=\"holiday-form\""),
        ("/leaves/workflows", ["admin"], "id=\"wf-form\""),
        ("/timesheets/", ["admin", "mgr", "emp"], "id=\"entry-form\""),
        ("/timesheets/approvals", ["admin", "mgr"], "Timesheet Approvals"),
        ("/timesheets/projects", ["admin"], "id=\"project-form\""),
        ("/ess/", ["admin", "mgr", "emp"], "id=\"tab-performance\""),
        ("/ess/loans", ["admin", "mgr", "emp"], "Request New Loan/Advance"),
        ("/ess/slips", ["admin", "mgr", "emp"], "Salary Slips"),
        ("/ess/performance", ["admin", "mgr", "emp"], "Active Goals"),
        ("/ess/change-requests", ["admin"], "Pending Change Requests"),
        ("/mss/", ["admin", "mgr"], "Manager Self-Service"),
        ("/mss/approvals", ["admin", "mgr"], "id=\"bulk-approve-ts\""),
        ("/mss/team", ["admin", "mgr"], "No team members"),
        ("/auth/users", ["admin", "mgr"], "id=\"pw-modal\""),
        ("/auth/members/assign", ["admin", "mgr"], "Assign Member as Employee"),
        ("/auth/profile", ["admin", "mgr", "emp"], "action=\"/auth/account-settings\""),
        ("/auth/change-password", ["admin", "mgr", "emp"], "name=\"confirm_password\""),
    ];

    [ReferenceFact]
    public async Task Every_page_renders_for_its_people_and_refuses_the_rest()
    {
        var w = await PagesWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var clients = new Dictionary<string, PageClient> { ["admin"] = w.Client(host, "admin"), ["mgr"] = w.Client(host, "mgr"), ["emp"] = w.Client(host, "emp") };
        foreach (var (url, open, marker) in Pages)
            foreach (var (who, c) in clients)
            {
                var r = await c.GetAsync(url);
                string body = await r.Content.ReadAsStringAsync();
                if (open.Contains(who))
                {
                    Assert.True(r.StatusCode == HttpStatusCode.OK, $"{who} {url}: {(int)r.StatusCode} {body[..Math.Min(body.Length, 600)]}");
                    if (!(url == "/mss/team" && who == "mgr"))
                        Assert.True(url == "/mss/team" || body.Contains(marker), $"{who} {url}: no {marker}");
                    Assert.Contains("Accountix ERP", body);                       // the HR topbar heading
                    Assert.Contains("href=\"/leaves/\"", body);                   // the HR sidebar
                    // data inside <script> is raw JSON (Jinja's |tojson), never HTML-encoded
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, "<script>(.*?)</script>",
                                 System.Text.RegularExpressions.RegexOptions.Singleline))
                        Assert.False(m.Groups[1].Value.Contains("&quot;") || m.Groups[1].Value.Contains("&amp;") || m.Groups[1].Value.Contains("&#x27;"),
                            $"{who} {url}: encoded text in a script block");
                }
                else
                {
                    Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
                    Assert.Equal("/dashboard", r.Headers.Location!.OriginalString);
                }
            }
        // the team page lists the manager's report; the sidebar's lead/admin links follow the role
        Assert.Contains("Pete Employee", await clients["mgr"].GetStringAsync("/mss/team"));
        string empNav = await clients["emp"].GetStringAsync("/dashboard");
        Assert.DoesNotContain("href=\"/mss/approvals\"", empNav);
        Assert.DoesNotContain("href=\"/attendance/admin\"", empNav);
        string mgrNav = await clients["mgr"].GetStringAsync("/dashboard");
        Assert.Contains("href=\"/mss/approvals\"", mgrNav);
        Assert.DoesNotContain("href=\"/auth/users\"", mgrNav);
        Assert.Contains("href=\"/auth/users\"", await clients["admin"].GetStringAsync("/dashboard"));
        // back links of hr_app's inject_back_urls
        Assert.Contains("href=\"/leaves/\" class=\"btn btn-link\"", await clients["emp"].GetStringAsync("/leaves/apply"));
        Assert.Contains("href=\"/mss/team\" class=\"btn btn-link\"", await clients["admin"].GetStringAsync($"/mss/evaluate/{w["emp"]}"));
        // pages show the data
        Assert.Contains("Founders Day", await clients["admin"].GetStringAsync("/leaves/holidays"));
        Assert.Contains("wedding", await clients["mgr"].GetStringAsync("/mss/approvals"));
        Assert.Contains("Rs. 15,000", await clients["emp"].GetStringAsync("/ess/loans"));
        Assert.Contains("0300-1234567", await clients["admin"].GetStringAsync("/ess/change-requests"));
        Assert.Contains("2.0h", await clients["emp"].GetStringAsync("/attendance/overview?period=monthly"));
    }

    [ReferenceFact]
    public async Task Json_endpoints_answer_pythons_shapes()
    {
        var w = await PagesWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var mgr = w.Client(host, "mgr");
        var emp = w.Client(host, "emp");
        string avail = await mgr.GetStringAsync("/mss/team-availability");
        Assert.Contains("\"name\":\"Pete Employee\"", avail);
        Assert.Contains("\"status\":\"on_duty\"", avail);
        Assert.Contains("\"status\":\"online\"", await mgr.GetStringAsync("/attendance/live"));
        string logs = await emp.GetStringAsync("/attendance/api/logs");
        Assert.Contains("\"is_late\":true", logs);
        Assert.Contains("T09:40:00", logs);
        var today = DateTime.Today;
        string cal = await emp.GetStringAsync($"/leaves/calendar?month={today.AddDays(20).Month}&year={today.AddDays(20).Year}");
        Assert.Contains("\"entries\":", cal);
        Assert.Contains("GMT", cal);           // Flask's JSON writes dates as HTTP dates
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync("/mss/team-calendar")).StatusCode);
        Assert.Equal("[]", await w.Client(host, "admin").GetStringAsync("/mss/team-calendar"));
    }

    [ReferenceFact]
    public async Task Avatar_and_profile_updates_go_through_stored_files_and_change_requests()
    {
        var w = await PagesWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var emp = w.Client(host, "emp");
        string token = await emp.TokenFromAsync("/ess/");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
        };
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");   // the browser's say-so is ignored
        form.Add(file, "profile_picture", "me.png");
        var r = await emp.PostAsync("/ess/upload-picture", form);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var u = await w.UserAsync("emp");
        Assert.StartsWith($"avatar_{u.Id}_", u.ProfileImage);
        var img = await emp.GetAsync($"/ess/avatar/{u.ProfileImage}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/png", img.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await img.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await emp.GetAsync("/ess/avatar/..%2F..%2Fsecret.png")).StatusCode);

        // the name changes at once; contact and bank fields wait for HR, once per field
        await World.PostAsync(emp, "/ess/update-profile", "/ess/", ("full_name", "Pete Employee-Khan"), ("phone", "0311-7654321"), ("bank_name", "HBL"));
        await World.PostAsync(emp, "/ess/update-profile", "/ess/", ("phone", "0311-0000000"));
        Assert.Equal("Pete Employee-Khan", (await w.UserAsync("emp")).FullName);
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            var crs = await db.Set<ChangeRequest>().AsNoTracking().Where(c => c.UserId == uid && c.Status == "pending").ToListAsync();
            Assert.Equal(["bank_name", "phone"], crs.Select(c => c.FieldName).Distinct().OrderBy(x => x).ToArray());
            Assert.Single(crs, c => c.FieldName == "phone");   // the first phone request is the one pending
        }
        // the admin approves one: it reaches the account
        var admin = w.Client(host, "admin");
        int crId;
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            crId = await db.Set<ChangeRequest>().Where(c => c.UserId == uid && c.FieldName == "bank_name").Select(c => c.Id).FirstAsync();
        }
        r = await World.PostAsync(admin, $"/ess/review-change/{crId}", "/ess/change-requests", ("action", "approve"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("HBL", (await w.UserAsync("emp")).BankName);
        r = await World.PostAsync(admin, $"/ess/review-change/{crId}", "/ess/change-requests", ("action", "reject"));
        Assert.Equal("HBL", (await w.UserAsync("emp")).BankName);   // reviewed once
    }

    [ReferenceFact]
    public async Task Loan_request_files_the_loan_its_account_and_tells_the_manager()
    {
        var w = await PagesWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        var emp = w.Client(host, "emp");
        var r = await World.PostAsync(emp, "/ess/loans", "/ess/loans", ("type", "loan"), ("amount", "100000"), ("purpose", "bike"), ("installments", "12"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await using var db = w.Db();
        int uid = w["emp"], mgr = w["mgr"];
        var loan = await db.Set<LoanAdvanceRequest>().AsNoTracking().Where(l => l.UserId == uid && l.Purpose == "bike").SingleAsync();
        Assert.Equal((100000m, 12, 8333.33m, 100000m, "pending"), (loan.Amount, loan.InstallmentMonths!.Value, loan.MonthlyInstallment!.Value, loan.RemainingAmount!.Value, loan.Status!));
        Assert.True(await db.Set<ChartOfAccount>().AnyAsync(a => a.Name == $"Loan #{loan.Id} - Pete Employee"));
        Assert.True(await db.Set<Notification>().AnyAsync(n => n.Message == "Pete Employee requests a loan of Rs.100,000" && n.ReferenceId == loan.Id));
        // refused shapes
        r = await World.PostAsync(emp, "/ess/loans", "/ess/loans", ("type", "loan"), ("amount", "5000"), ("purpose", "x"), ("installments", "0"));
        Assert.Equal("/ess/loans", r.Headers.Location!.OriginalString);
    }
}
