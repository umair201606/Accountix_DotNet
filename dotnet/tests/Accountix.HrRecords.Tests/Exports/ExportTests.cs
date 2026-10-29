using System.Net;
using System.Text;
using Accountix.TestSupport;
using ClosedXML.Excel;

namespace Accountix.HrRecords.Tests.Exports;

/// <summary>
/// The HR downloads through Finance's IReportExport (attendance, leave, the
/// report builder's result): CSV equal to Python's line for line
/// (Golden/big_office.json, captured on the same BigOffice data), Excel and
/// PDF built with the shared layout, file names as Python names them, the
/// same refusals. Needs Accountix.Finance (build with -p:WithFinance=false to
/// leave these out while it does not compile).
/// </summary>
public class ExportTests
{
    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly, typeof(Accountix.Finance.FinanceModule).Assembly);

    private static string FileName(string disposition)
    {
        var m = System.Text.RegularExpressions.Regex.Match(disposition, "filename=\"?([^\";]+)\"?");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>The file name without the company part (Python names it from the CompanyInfo profile), today as TODAY.</summary>
    private static string Tail(string name, string title) =>
        name[name.IndexOf(title, StringComparison.Ordinal)..].Replace(BigOffice.Today.ToString("yyyy-MM-dd"), "TODAY");

    private static string[] SortedLines(byte[] csv) =>
        Encoding.UTF8.GetString(csv).TrimStart('﻿').Split("\r\n").Select((l, i) => i == 0 ? "0" + l : "1" + l).Order(StringComparer.Ordinal).ToArray();

    [ReferenceFact]
    public async Task Attendance_and_leave_csv_equal_pythons()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await HostAsync();
        foreach (var g in Golden.Load("big_office").GetProperty("downloads").EnumerateArray())
        {
            string who = g.GetProperty("who").GetString()!, url = g.GetProperty("url").GetString()!;
            var r = await w.Client(host, who).GetAsync(url);
            byte[] net = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(g.GetProperty("status").GetInt32(), (int)r.StatusCode);
            Assert.Equal("text/csv", r.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("text/csv", g.GetProperty("type").GetString());
            // Rows of one day (or one start date) come in the database's order in Python: compare the lines.
            Golden.SameRows(g.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToList(), SortedLines(net), $"{who} {url}");
            string title = url.Contains("attendance") ? "Attendance_Report" : "Leave_Report";
            Assert.Equal(g.GetProperty("file").GetString(), Tail(r.Content.Headers.ContentDisposition!.FileName!.Trim('"'), title));
        }
    }

    [ReferenceFact]
    public async Task The_report_builder_download_equals_pythons()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await HostAsync();
        var admin = w.Client(host, "admin");
        string token = await admin.TokenFromAsync("/reports/");
        var q = await admin.PostJsonWithTokenAsync("/reports/query",
            new { scope = "department", department = "Finance", columns = new[] { "employee", "total_hours", "leaves", "late_days", "pf_balance" } }, token);
        var results = World.Json(await q.Content.ReadAsStringAsync()).GetProperty("results");
        // As the page sends it, plus a typed figure with a thousands comma.
        string rows = results.GetRawText().Replace("\"late_days\":", "\"note\":\"1,234.5\",\"late_days\":");
        foreach (var g in Golden.Load("big_office").GetProperty("builder_download").EnumerateArray())
        {
            string fmt = g.GetProperty("format").GetString()!;
            string body = $$"""{"rows":{{rows}},"columns":["employee","total_hours","leaves","late_days","pf_balance","note"],"date_from":"2026-01-01","date_to":null,"format":"{{fmt}}"}""";
            var r = await admin.PostJsonWithTokenAsync("/reports/export-excel", World.Json(body), token);
            byte[] net = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal((g.GetProperty("status").GetInt32(), 200), ((int)r.StatusCode, 200));
            Assert.Equal(g.GetProperty("file").GetString(), Tail(r.Content.Headers.ContentDisposition!.FileName!.Trim('"'), "HR_Report"));
            if (fmt == "csv")
                Golden.SameText(g.GetProperty("csv").GetString()!, Encoding.UTF8.GetString(net), "the report builder's CSV");
            else if (fmt == "pdf")
                Assert.StartsWith("%PDF", Encoding.ASCII.GetString(net, 0, 4));
            else
            {
                using var wb = new XLWorkbook(new MemoryStream(net));
                var ws = wb.Worksheets.First();
                var cells = ws.CellsUsed().Select(c => c.GetFormattedString()).ToList();
                foreach (string h in new[] { "Employee", "Total Hours", "Leaves", "Late Days", "PF Balance", "Note", "HR Report", "2026-01-01 to today" })
                    Assert.Contains(cells, c => c.Contains(h));
            }
        }
    }

    [ReferenceFact]
    public async Task Downloads_are_refused_as_python_refuses_them()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await HostAsync();
        var rmgr = w.Client(host, "rmgr");
        var r = await rmgr.GetAsync("/reports/export-attendance?format=csv");
        Assert.Equal((HttpStatusCode.Forbidden, "{\"error\":\"You do not have the Attendance view right.\"}\n"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
        r = await rmgr.GetAsync("/reports/export-leaves?format=csv");
        Assert.Equal("{\"error\":\"You do not have the Leaves view right.\"}\n", await r.Content.ReadAsStringAsync());
        r = await w.Client(host, "e1").GetAsync("/reports/export-leaves");
        Assert.Equal("{\"error\":\"Access denied\"}\n", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Client(host, "admin").GetAsync("/reports/export-leaves?format=doc")).StatusCode);

        var admin = w.Client(host, "admin");
        string token = await admin.TokenFromAsync("/reports/");
        r = await admin.PostJsonWithTokenAsync("/reports/export-excel", new { }, token);
        Assert.Equal((HttpStatusCode.BadRequest, "{\"error\":\"No data supplied for export.\"}\n"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
        r = await admin.PostJsonWithTokenAsync("/reports/export-excel", new { rows = Array.Empty<object>(), columns = Array.Empty<string>() }, token);
        Assert.Equal("{\"error\":\"No columns to export.\"}\n", await r.Content.ReadAsStringAsync());

        // Excel and PDF of the month: real files.
        var today = BigOffice.Anchor;
        var x = await admin.GetAsync($"/reports/export-attendance?month={today.Month}&year={today.Year}&format=excel");
        using (var wb = new XLWorkbook(new MemoryStream(await x.Content.ReadAsByteArrayAsync())))
            Assert.Contains(wb.Worksheets.First().CellsUsed(), c => c.GetString() == "Half Day");
        var p = await admin.GetAsync($"/reports/export-leaves?year={today.Year}&format=pdf");
        Assert.Equal("application/pdf", p.Content.Headers.ContentType!.MediaType);
    }
}
