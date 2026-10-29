using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Fbr.Tests;

/// <summary>
/// Reporting an invoice to FBR cannot be taken back: six people pressing
/// Send together, and Retries and Sends together, each report it exactly
/// once. And the submissions list stays quick with thousands of rows.
/// (Python's own timing of the list, 250–258 ms, is kept in docs/modules/fbr.md as history.)
/// </summary>
public class ManyUsersAndSpeedTests
{
    private const string Accepted = """{"fbrInvoiceNumber": "7000007DI0000000000001", "submissionId": "S-1"}""";

    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(FbrModule).Assembly, typeof(FakeFbrModule).Assembly);

    [ReferenceFact]
    public async Task Six_people_pressing_send_together_report_the_invoice_once()
    {
        await FbrData.ConfigureAsync();
        int id = await FbrData.SimpleInvoiceAsync("race6");
        await using var host = await HostAsync();
        var clients = host.ClientsFor(FbrData.OwnerB, FbrData.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/fbr/")));
        // FBR takes its time: the other five arrive while the first is still waiting for it.
        FakeFbr.Reset(FakeFbr.Json(200, Accepted, delayMs: 400));

        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/fbr/submit/{id}", [], tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, FakeFbr.Sends);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var r in rs.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("already submitted successfully", await r.Content.ReadAsStringAsync());
        }
        var rows = await FbrData.RecordsAsync(id);
        Assert.Single(rows);
        Assert.Equal("success", rows[0].Status);
    }

    [ReferenceFact]
    public async Task A_retry_and_a_send_together_report_the_invoice_once()
    {
        await FbrData.ConfigureAsync();
        int id = await FbrData.SimpleInvoiceAsync("retrysend");
        await using var host = await HostAsync();
        var first = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
        FakeFbr.Reset(FakeFbr.Json(500, """{"error": "down"}"""));
        var failed = await first.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        int recordId = (await FbrData.RecordsAsync(id)).Single().Id;

        var clients = host.ClientsFor(FbrData.OwnerB, FbrData.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/fbr/")));
        FakeFbr.Reset(FakeFbr.Json(200, Accepted, delayMs: 300));
        // Three retries of the failed record and three Sends, all at once.
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync(
            i % 2 == 0 ? $"/fbr/retry/{recordId}" : $"/fbr/submit/{id}", [], tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, FakeFbr.Sends);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        var rows = await FbrData.RecordsAsync(id);
        Assert.Equal(1, rows.Count(r => r.Status == "success"));
    }

    /// <summary>
    /// 3,000 submissions (a year of a busy company, several attempts each):
    /// the list is one query for the rows however many there are.
    /// </summary>
    [ReferenceFact]
    public async Task The_submissions_list_runs_a_fixed_number_of_queries_and_is_quick()
    {
        await FbrData.ConfigureAsync();
        int invoice = await FbrData.SimpleInvoiceAsync("bulk");
        await using (var db = FbrData.Db())
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < 3000; i++)
                db.Add(new FbrInvoiceRecord
                {
                    InvInvoiceId = invoice, Status = i % 3 == 0 ? "failed" : "success", InvoiceType = "S", AttemptCount = 1 + i % 4,
                    FbrInvoiceNumber = $"7000007DI{i:D13}", FbrSubmissionId = $"S-{i}", RequestJson = "{}", ResponseJson = "{}",
                    ErrorMessage = i % 3 == 0 ? "Provide proper HS Code" : "", SubmittedAt = now, SubmittedBy = FbrData.OwnerB, CreatedAt = now,
                });
            await db.SaveChangesAsync();
        }
        try
        {
            await using var host = await HostAsync();
            var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
            FakeFbr.Reset(FakeFbr.Json(200, """{"status": "Valid"}"""));      // the status page asks FBR: answer at once
            var all = (await host.MeasureAsync(c, "/fbr/")).Within(800, 12);
            Assert.Contains("7000007DI0000000002999", all.Body);
            (await host.MeasureAsync(c, "/fbr/?status=failed")).Within(800, 12);
            (await host.MeasureAsync(c, "/fbr/settings/")).Within(800, 12);
            (await host.MeasureAsync(c, $"/fbr/status/{invoice}")).Within(800, 12);
        }
        finally
        {
            await FbrData.DeleteRecordsAsync(invoice);
        }
    }

    /// <summary>The full-year database (read only): the dashboard and an invoice's payload are quick.</summary>
    [PerfFact]
    public async Task The_dashboard_on_a_full_year_is_quick()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(FbrModule).Assembly, typeof(FakeFbrModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        (await host.MeasureAsync(c, "/fbr/")).Within(800, 12);
        (await host.MeasureAsync(c, "/fbr/settings/")).Within(800, 12);

        var company = new Accountix.Core.Data.CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        int busiest = await db.Set<InvInvoiceItem>().GroupBy(i => i.InvoiceId).OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstAsync();
        var clock = new Accountix.Core.Shared.CompanyClock(db, company, TimeProvider.System);
        var mapper = new Services.FbrMapper(db, clock);
        var config = new FbrConfig { Environment = "sandbox", IsActive = true, AccessToken = "t" };
        await mapper.BuildAsync(busiest, config);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            await mapper.BuildAsync(busiest, config);
        Console.WriteLine($"FBR payload of the invoice with the most lines ({busiest}): {sw.Elapsed.TotalMilliseconds / 5:F1} ms");
        Assert.True(sw.ElapsedMilliseconds / 5 < 200, $"payload build {sw.ElapsedMilliseconds / 5} ms");
    }
}
