namespace Accountix.HrRecords.Services;

/// <summary>
/// The ad-hoc report builder (hr_app/routes/reports.py query()): one row per
/// person in scope with the figures asked for. Python asked once per person
/// per column; here each column is one grouped query over everyone in scope,
/// so a company of any size costs the same handful of queries. The figures
/// are Python's to the last digit, its quirks included:
/// <list type="bullet">
/// <item>total_hours is the hours worked: epoch seconds / 3600 per clocked-out
/// day (both apps used to multiply the Postgres figure by 24 again, a factor
/// meant for SQLite's julianday days: 8 hours read 192);</item>
/// <item>hours are summed in double precision (SQLAlchemy divides by CAST(3600.0 AS FLOAT));</item>
/// <item>leaves / sick_days are 0 (a whole number) when there is no approved
/// request, else the float sum of the days; the date filter keeps requests that
/// start on or after "from" and end on or before "to";</item>
/// <item>overtime, PF and loan balances ignore the dates;</item>
/// <item>everything is rounded as Python's round(float, 2).</item>
/// </list>
/// </summary>
public sealed class HrReport(AccountixDbContext db)
{
    /// <summary>Report columns that read another section's data need that section's view right.</summary>
    public static readonly IReadOnlyDictionary<string, string> ColumnRights = new Dictionary<string, string>
    {
        ["pf_balance"] = "pf", ["loan_balance"] = "loans", ["leaves"] = "leaves", ["sick_days"] = "leaves",
    };

    public static readonly string[] DefaultColumns = ["employee", "total_hours", "leaves", "sick_days"];

    private sealed record Sum(int UserId, double? Value);

    private sealed record Counts(int UserId, int Late, int Present);

    /// <summary>The rows (dictionaries keyed as Python's JSON), in the order of <paramref name="people"/>.</summary>
    public async Task<List<Dictionary<string, object?>>> RowsAsync(IReadOnlyList<Person> people, IReadOnlyCollection<string> columns,
                                                                   DateOnly? from, DateOnly? to, int companyId, CancellationToken ct = default)
    {
        var rows = people.Select(p => new Dictionary<string, object?>
        {
            ["employee"] = p.FullName, ["department"] = p.Department, ["id"] = p.Id,
        }).ToList();
        if (people.Count == 0) return rows;
        int[] ids = people.Select(p => p.Id).Distinct().ToArray();
        // No date given: no bound (every stored date lies between these).
        DateOnly lo = from ?? DateOnly.MinValue, hi = to ?? DateOnly.MaxValue;
        bool Has(string c) => columns.Contains(c);

        if (Has("total_hours"))
        {
            var hours = await db.Database.SqlQuery<Sum>($"""
                SELECT a.user_id AS "UserId",
                       sum(CASE WHEN (a.clock_out IS NOT NULL)
                                THEN EXTRACT(epoch FROM a.clock_out - a.clock_in) / CAST(3600.0 AS FLOAT) ELSE 0 END) AS "Value"
                FROM attendance a
                WHERE a.company_id = {companyId} AND a.user_id = ANY({ids})
                  AND (a.date >= {lo}) AND (a.date <= {hi})
                GROUP BY a.user_id
                """).ToDictionaryAsync(r => r.UserId, r => r.Value ?? 0, ct);
            foreach (var row in rows)
                row["total_hours"] = Py.Round(hours.GetValueOrDefault((int)row["id"]!), 2);
        }
        if (Has("overtime"))
        {
            var ot = await db.Database.SqlQuery<Sum>($"""
                SELECT o.user_id AS "UserId", sum(o.overtime_hours) AS "Value"
                FROM overtime_accounts o
                WHERE o.company_id = {companyId} AND o.user_id = ANY({ids})
                GROUP BY o.user_id
                """).ToDictionaryAsync(r => r.UserId, r => r.Value ?? 0, ct);
            foreach (var row in rows)
                row["overtime"] = Py.Round(ot.GetValueOrDefault((int)row["id"]!), 2);
        }
        if (Has("leaves") || Has("sick_days"))
        {
            // One pass over the approved requests in range: all of them, and the sick ones (type code SL).
            var leave = await db.Database.SqlQuery<LeaveSums>($"""
                SELECT l.user_id AS "UserId",
                       CAST(count(*) AS integer) AS "AllCount",
                       sum(CAST(l.total_days AS double precision)) AS "AllDays",
                       CAST(count(*) FILTER (WHERE t.code = 'SL') AS integer) AS "SickCount",
                       sum(CAST(l.total_days AS double precision)) FILTER (WHERE t.code = 'SL') AS "SickDays"
                FROM leave_requests l
                LEFT JOIN leave_types t ON t.id = l.leave_type_id AND t.company_id = {companyId}
                WHERE l.company_id = {companyId} AND l.user_id = ANY({ids}) AND l.status = 'approved'
                  AND (l.start_date >= {lo}) AND (l.end_date <= {hi})
                GROUP BY l.user_id
                """).ToDictionaryAsync(r => r.UserId, ct);
            foreach (var row in rows)
            {
                leave.TryGetValue((int)row["id"]!, out var s);
                if (Has("leaves"))
                    row["leaves"] = s is { AllCount: > 0 } ? (object)(s.AllDays ?? 0) : 0;      // Python: sum([]) is the int 0
                if (Has("sick_days"))
                    row["sick_days"] = s is { SickCount: > 0 } ? (object)(s.SickDays ?? 0) : 0;
            }
        }
        if (Has("late_days") || Has("present_days"))
        {
            var counts = await db.Database.SqlQuery<Counts>($"""
                SELECT a.user_id AS "UserId",
                       CAST(count(*) FILTER (WHERE a.is_late = true) AS integer) AS "Late",
                       CAST(count(*) FILTER (WHERE a.clock_in IS NOT NULL) AS integer) AS "Present"
                FROM attendance a
                WHERE a.company_id = {companyId} AND a.user_id = ANY({ids})
                  AND (a.date >= {lo}) AND (a.date <= {hi})
                GROUP BY a.user_id
                """).ToDictionaryAsync(r => r.UserId, ct);
            foreach (var row in rows)
            {
                counts.TryGetValue((int)row["id"]!, out var c);
                if (Has("late_days")) row["late_days"] = c?.Late ?? 0;
                if (Has("present_days")) row["present_days"] = c?.Present ?? 0;
            }
        }
        if (Has("pf_balance"))
        {
            var pf = await db.Database.SqlQuery<Sum>($"""
                SELECT p.user_id AS "UserId", CAST(sum(p.credit) - sum(p.debit) AS double precision) AS "Value"
                FROM pf_ledger p
                WHERE p.company_id = {companyId} AND p.user_id = ANY({ids})
                GROUP BY p.user_id
                """).ToDictionaryAsync(r => r.UserId, r => r.Value ?? 0, ct);
            foreach (var row in rows)
                row["pf_balance"] = Py.Round(pf.GetValueOrDefault((int)row["id"]!), 2);
        }
        if (Has("loan_balance"))
        {
            var loans = await db.Database.SqlQuery<Sum>($"""
                SELECT r.user_id AS "UserId", CAST(sum(r.remaining_amount) AS double precision) AS "Value"
                FROM loan_advance_requests r
                WHERE r.company_id = {companyId} AND r.user_id = ANY({ids}) AND r.status = 'approved'
                GROUP BY r.user_id
                """).ToDictionaryAsync(r => r.UserId, r => r.Value ?? 0, ct);
            foreach (var row in rows)
                row["loan_balance"] = Py.Round(loans.GetValueOrDefault((int)row["id"]!), 2);
        }
        return rows;
    }

    private sealed record LeaveSums(int UserId, int AllCount, double? AllDays, int SickCount, double? SickDays);
}
