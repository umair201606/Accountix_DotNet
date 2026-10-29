namespace Accountix.Hr.Controllers;

/// <summary>The HR dashboard: the bare /dashboard route of hr_app/app.py.</summary>
[Area("Hr")]
[Module(Modules.Hr)]
public sealed class DashboardController(AccountixDbContext db) : Controller
{
    public sealed record WeekPoint(int Week, int Total, int Late, int Half, double Pct, bool PctIsInt, string Label);

    public sealed class Model
    {
        public bool Lead { get; init; }
        public string FullName { get; init; } = "";
        public IReadOnlyList<WeekPoint> Chart { get; init; } = [];
    }

    private sealed record WeekRow(int Week, int Year, int Total, int Late, int Half);

    [HttpGet("/dashboard")]
    [AnyMember]   // HR is governed by role in the route: the chart is for admins and managers
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var access = HttpContext.Access()!;
        bool lead = access.IsAdmin || access.IsManager;
        var chart = new List<WeekPoint>();
        if (lead)
        {
            int cid = access.CompanyId!.Value;
            int year = DateTime.Today.Year;     // Python: date.today() (the server's date)
            // Weekday attendance per ISO week of this year (Postgres extract(week / dow)), this company only.
            var weekly = await db.Database.SqlQuery<WeekRow>($"""
                SELECT CAST(extract(week FROM a.date) AS integer) AS "Week",
                       CAST(extract(year FROM a.date) AS integer) AS "Year",
                       CAST(count(a.id) AS integer) AS "Total",
                       CAST(sum(CASE WHEN a.is_late = true THEN 1 ELSE 0 END) AS integer) AS "Late",
                       CAST(sum(CASE WHEN a.is_half_day = true THEN 1 ELSE 0 END) AS integer) AS "Half"
                FROM attendance a
                WHERE a.company_id = {cid} AND extract(year FROM a.date) = {year}
                  AND extract(dow FROM a.date) IN (1, 2, 3, 4, 5)
                GROUP BY 2, 1 ORDER BY 2, 1
                """).ToListAsync(ct);
            int empCount = await (from m in db.Memberships
                                  where m.CompanyId == cid && m.Status == CompanyMembership.StatusActive
                                  join u in db.Users on m.UserId equals u.Id
                                  where u.IsActive == true
                                  select u.Id).CountAsync(ct);
            foreach (var r in weekly)
            {
                var jan1 = new DateOnly(r.Year, 1, 1);
                int toMonday = (7 - ((int)jan1.DayOfWeek + 6) % 7) % 7;
                var monday = jan1.AddDays(toMonday).AddDays(7 * (r.Week - 1));
                int weekdays = Enumerable.Range(0, 7).Count(d => monday.AddDays(d).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
                int possible = empCount * weekdays;
                double pct = possible > 0 ? Services.Py.R((double)r.Total / possible * 100, 1) : 0;
                chart.Add(new WeekPoint(r.Week, r.Total, r.Late, r.Half, pct, possible == 0,
                    monday.ToString("MMM dd", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return View(new Model { Lead = lead, FullName = access.User.FullName, Chart = chart });
    }
}
