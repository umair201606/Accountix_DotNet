using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Accountix.TestSupport;
using ClosedXML.Excel;

namespace Accountix.Executive.Tests;

/// <summary>The register, party-ledger and Books Integrity files (Finance's export pipeline), against Python's.</summary>
public class ExportTests
{
    private static int B => Exec.Company;

    private static List<List<string>> Csv(byte[] body) => Csv(Encoding.UTF8.GetString(body));

    private static List<List<string>> Csv(string text)
    {
        text = text.TrimStart('\uFEFF');
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = [];
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>Same cells, numbers compared as numbers (Python writes 1000.0, .NET 1000.00).</summary>
    private static void SameCsv(List<List<string>> py, List<List<string>> net, string what)
    {
        Assert.True(py.Count == net.Count, $"{what}: {py.Count} rows in Python, {net.Count} in .NET");
        for (int r = 0; r < py.Count; r++)
        {
            Assert.True(py[r].Count == net[r].Count, $"{what} row {r}: {string.Join(",", py[r])} vs {string.Join(",", net[r])}");
            for (int c = 0; c < py[r].Count; c++)
            {
                string a = py[r][c], b = net[r][c];
                if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    Assert.True(Math.Abs(x - y) < 0.005m, $"{what} [{r},{c}]: Python {a}, .NET {b}");
                else
                    Assert.True(a == b, $"{what} [{r},{c}]: Python '{a}', .NET '{b}'");
            }
        }
    }

    /// <summary>
    /// The CSV files equal Python's for the same data cell for cell, with the same file names
    /// (Golden/Exports.json: Python's files captured on the golden company, its clock at <see cref="Exec.Now"/>).
    /// </summary>
    [ReferenceFact]
    public async Task Register_party_and_integrity_csv_files_carry_what_python_writes()
    {
        var (td, _) = await Exec.SeedAsync();
        var codes = await Exec.CodesAsync(B);
        int c7 = await Exec.AccountIdAsync("1-01-02-01-9107", B);
        var golden = Golden.Load("Exports");
        await using var host = await Hosts.ExecutiveAsync();
        var c = host.ClientFor(Exec.OwnerB, B);
        foreach (string url in new[]
                 {
                     "/executive/receivables?format=csv", "/executive/payables?format=csv&as_of=2026-06-30",
                     "/executive/receivables?format=csv&q=customer&group=" + td,
                     $"/executive/party/{c7}?format=csv", $"/executive/party/{c7}?format=csv&as_of=2025-12-31",
                     "/executive/integrity?format=csv",
                 })
        {
            string key = Exec.LinkCodes(url, codes);
            var py = golden[key] ?? throw new InvalidOperationException($"no golden file for {key}");
            var r = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
            string name = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "";
            Assert.Equal(py["file_name"]!.GetValue<string>(), name);
            SameCsv(Csv(py["csv"]!.GetValue<string>()), Csv(await r.Content.ReadAsByteArrayAsync()), key);
        }
    }

    [ReferenceFact]
    public async Task Excel_and_pdf_files_open_with_their_sheets_and_headings()
    {
        await Exec.SeedAsync();
        await using var host = await Hosts.ExecutiveAsync();
        var c = host.ClientFor(Exec.OwnerB, B);

        var x = await c.GetAsync("/executive/receivables?format=excel&as_of=2026-06-30");
        Assert.Equal(HttpStatusCode.OK, x.StatusCode);
        using (var wb = new XLWorkbook(new MemoryStream(await x.Content.ReadAsByteArrayAsync())))
        {
            var ws = wb.Worksheets.First();
            var text = ws.CellsUsed().Select(cell => cell.GetString()).ToList();
            Assert.Contains("Aged Receivables", text);
            Assert.Contains("Balances as at 30 Jun 2026 · FIFO aging", text);
            Assert.Contains("90+ days", text);
            Assert.Contains(text, t => t.StartsWith("Total (", StringComparison.Ordinal));
        }

        var i = await c.GetAsync("/executive/integrity?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, i.StatusCode);
        using (var wb = new XLWorkbook(new MemoryStream(await i.Content.ReadAsByteArrayAsync())))
        {
            Assert.Equal(["Books Integrity", "Recent re-costings"], wb.Worksheets.Select(w => w.Name));
            var text = wb.Worksheets.First().CellsUsed().Select(cell => cell.GetString()).ToList();
            Assert.Contains(text, t => t.StartsWith("All checks pass · run ", StringComparison.Ordinal));
            Assert.Contains("Trial balance (debits = credits)", text);
        }

        foreach (string url in new[] { "/executive/payables?format=pdf", "/executive/integrity?format=pdf" })
        {
            var p = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, p.StatusCode);
            Assert.Equal("application/pdf", p.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await p.Content.ReadAsByteArrayAsync())[..4]));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/executive/receivables?format=docx")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/executive/integrity?format=docx")).StatusCode);
    }
}
