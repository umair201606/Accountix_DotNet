using System.Diagnostics;
using System.Text;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Accountix.Finance.Services;
using ClosedXML.Excel;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>The export pipeline on 10,000+ rows: linear work only (no per-cell O(n²)), within the export budget.</summary>
public class ReportExportSpeedTests(ITestOutputHelper output)
{
    private static ReportExport Exporter() => new(new Microsoft.AspNetCore.Http.HttpContextAccessor(),
        new CompanyClock(null!, new Accountix.Core.Data.CompanyContext(), TimeProvider.System));

    private static ReportTable Ledger(int n)
    {
        var rows = new List<ReportRow>(n + n / 50 * 2);
        for (int i = 0; i < n; i++)
        {
            if (i % 50 == 0) rows.Add(new ReportRow([$"1-01-01-01-{i / 50:D4}  Account {i / 50}", "", "", "", "", ""], "section"));
            rows.Add(new ReportRow([new DateOnly(2026, 5, 1 + i % 28).ToString("yyyy-MM-dd"), $"SI-202605-{i:D5}", $"Sale to customer {i % 97}, invoice line",
                                    1000m + i % 313 * 7.25m, i % 3 == 0 ? 0m : 125.5m, 50000m - i * 3.1m], "account"));
            if (i % 50 == 49) rows.Add(new ReportRow(["", "", "Closing Balance (Dr)", "", "", 12345.67m], "grand"));
        }
        return new ReportTable
        {
            Title = "General Ledger", Period = "For the period 01 May, 2026 to 31 May, 2026",
            Columns = [new("Date"), new("Voucher #"), new("Description"), new("Debit", ReportColumnKind.Money),
                       new("Credit", ReportColumnKind.Money), new("Balance", ReportColumnKind.Money)],
            Rows = rows,
        };
    }

    private static (TimeSpan Elapsed, byte[] Bytes) Time(Func<byte[]> build)
    {
        build();   // warm-up (fonts, JIT)
        var sw = Stopwatch.StartNew();
        var bytes = build();
        return (sw.Elapsed, bytes);
    }

    [Fact]
    public void Ten_thousand_rows_export_within_the_budget_and_grow_linearly()
    {
        var x = Exporter();
        var small = Ledger(2_500);
        var big = Ledger(10_000);
        var (excel2, _) = Time(() => x.BuildExcel(small));
        var (excel10, xlsx) = Time(() => x.BuildExcel(big));
        var (csv10, csv) = Time(() => ReportExport.BuildCsv(big));
        var (pdf2, _) = Time(() => x.BuildPdf(small));
        var (pdf10, pdf) = Time(() => x.BuildPdf(big));
        output.WriteLine($"Excel 2.5k {excel2.TotalMilliseconds:F0} ms, 10k {excel10.TotalMilliseconds:F0} ms ({xlsx.Length / 1024} KB)");
        output.WriteLine($"CSV 10k {csv10.TotalMilliseconds:F0} ms ({csv.Length / 1024} KB)");
        output.WriteLine($"PDF 2.5k {pdf2.TotalMilliseconds:F0} ms, 10k {pdf10.TotalMilliseconds:F0} ms ({pdf.Length / 1024} KB)");

        Assert.True(excel10.TotalMilliseconds < 3000, $"Excel 10k rows took {excel10.TotalMilliseconds:F0} ms");
        Assert.True(csv10.TotalMilliseconds < 500, $"CSV 10k rows took {csv10.TotalMilliseconds:F0} ms");
        // Four times the rows may take at most ~6x the time (linear plus noise), never the 16x of O(n²).
        Assert.True(excel10.TotalMilliseconds < excel2.TotalMilliseconds * 6 + 200, "Excel grows faster than linearly");
        Assert.True(pdf10.TotalMilliseconds < pdf2.TotalMilliseconds * 6 + 500, "PDF grows faster than linearly");

        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet(1);
        Assert.Equal(5 + big.Rows.Count, ws.LastRowUsed()!.RowNumber());
        Assert.Equal(Money.ExcelWestern, ws.Cell(7, 4).Style.NumberFormat.Format);
        Assert.True(ws.Cell(7, 4).Style.Border.LeftBorder == XLBorderStyleValues.Thin);
        Assert.True(ws.Cell(6, 1).Style.Font.Bold);   // section band
    }

    private static ReportTable Wide(int n)
    {
        var cols = new List<ReportColumn> { new("Number"), new("Date"), new("Customer"), new("Label") };
        cols.AddRange(Enumerable.Range(1, 11).Select(i => new ReportColumn($"Figure {i}", ReportColumnKind.Money)));
        var rows = new List<ReportRow>(n + 1);
        for (int i = 0; i < n; i++)
        {
            var cells = new List<object?> { $"SI-202605-{i:D5}", new DateOnly(2026, 5, 1 + i % 28), $"Customer {i % 211} (Pvt) Ltd", i % 5 == 0 ? null : "Project A" };
            cells.AddRange(Enumerable.Range(1, 11).Select(k => (object?)(i * 1.5m + k * 100.25m)));
            rows.Add(new ReportRow(cells));
        }
        rows.Add(new ReportRow(["", "", "Total", "", .. Enumerable.Range(1, 11).Select(k => (object?)(n * 10m + k))], "grand"));
        return new ReportTable { Title = "Invoice Register", Period = "May 2026", Columns = cols, Rows = rows, MonoColumn = 0 };
    }

    [Fact]
    public void Ten_thousand_rows_by_fifteen_columns_meet_the_coordinator_targets()
    {
        var x = Exporter();
        var t = Wide(10_000);
        var (excel, xlsx) = Time(() => x.BuildExcel(t));
        var (pdf, bytes) = Time(() => x.BuildPdf(t));
        var (csv, _) = Time(() => ReportExport.BuildCsv(t));
        output.WriteLine($"10k × 15: Excel {excel.TotalMilliseconds:F0} ms ({xlsx.Length / 1024} KB), PDF {pdf.TotalMilliseconds:F0} ms ({bytes.Length / 1024} KB), CSV {csv.TotalMilliseconds:F0} ms");
        Assert.True(excel.TotalMilliseconds <= 1000, $"Excel took {excel.TotalMilliseconds:F0} ms");
        Assert.True(pdf.TotalMilliseconds <= 3000, $"PDF took {pdf.TotalMilliseconds:F0} ms");

        // The streamed workbook reads back with the layout BuildExcel always had.
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet("Invoice Register");
        Assert.Equal("Invoice Register", ws.Cell(2, 1).GetString());
        Assert.Equal("May 2026", ws.Cell(3, 1).GetString());
        Assert.StartsWith("Generated ", ws.Cell(4, 1).GetString());
        Assert.True(ws.Range(1, 1, 1, 15).IsMerged());
        Assert.Equal("Figure 11", ws.Cell(5, 15).GetString());
        Assert.True(ws.Cell(5, 1).Style.Font.Bold);
        Assert.Equal(30, ws.Row(5).Height);
        Assert.Equal(5, ws.SheetView.SplitRow);
        Assert.Equal("SI-202605-00000", ws.Cell(6, 1).GetString());
        Assert.Equal(new DateTime(2026, 5, 1), ws.Cell(6, 2).GetDateTime());
        Assert.Equal("dd-mmm-yyyy", ws.Cell(6, 2).Style.NumberFormat.Format);
        Assert.Equal(100.25, ws.Cell(6, 5).GetDouble());
        Assert.Equal(Money.ExcelWestern, ws.Cell(6, 5).Style.NumberFormat.Format);
        Assert.Equal(XLAlignmentHorizontalValues.Right, ws.Cell(6, 5).Style.Alignment.Horizontal);
        Assert.Equal(XLBorderStyleValues.Thin, ws.Cell(6, 3).Style.Border.BottomBorder);
        int last = 5 + t.Rows.Count;
        Assert.Equal("Total", ws.Cell(last, 3).GetString());
        Assert.Equal(XLColor.FromHtml("#1E293B"), ws.Cell(last, 3).Style.Fill.BackgroundColor);
        Assert.Equal(XLBorderStyleValues.Double, ws.Cell(last, 3).Style.Border.BottomBorder);
        Assert.Equal($"A5:O{last - 1}", ws.AutoFilter.Range.RangeAddress.ToString());   // stops above the grand total
        Assert.Equal(XLPageOrientation.Landscape, ws.PageSetup.PageOrientation);
        Assert.Equal(XLPaperSize.A4Paper, ws.PageSetup.PaperSize);
        Assert.Equal(5, ws.PageSetup.FirstRowToRepeatAtTop);
        Assert.False(ws.ShowGridLines);
        // "Customer 210 (Pvt) Ltd" (22) + 2, from the text itself, as openpyxl writes it (ClosedXML reads widths back less its padding).
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(xlsx)))
        using (var sheetXml = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
            Assert.Contains("<col min=\"3\" max=\"3\" width=\"24\" customWidth=\"1\"/>", sheetXml.ReadToEnd());
        Assert.Equal("Invoice Register", wb.Properties.Title);
    }

    [Fact]
    public void Extra_list_sheets_are_streamed_too()
    {
        var t = new ReportTable
        {
            Title = "Books Integrity", Period = "All checks pass", Columns = [new("Check"), new("Status")],
            Rows = [new ReportRow(["Trial balance", "Pass"])],
            ExtraSheets = [("Recent re-costings", ["Document", "Posted on", "Was"], [["SI-1", new DateTime(2026, 5, 2), 12.5m], ["SI-2", null, 1m]])],
        };
        using var wb = new XLWorkbook(new MemoryStream(Exporter().BuildExcel(t)));
        var ws = wb.Worksheet("Recent re-costings");
        Assert.Equal("Books Integrity — Recent re-costings", ws.Cell(2, 1).GetString());
        Assert.Equal("Was", ws.Cell(5, 3).GetString());
        Assert.Equal(12.5, ws.Cell(6, 3).GetDouble());
        Assert.Equal(Money.ExcelWestern, ws.Cell(6, 3).Style.NumberFormat.Format);
        Assert.Equal("dd-mmm-yyyy", ws.Cell(6, 2).Style.NumberFormat.Format);
        Assert.Equal("A5:C7", ws.AutoFilter.Range.RangeAddress.ToString());
    }

    [Fact]
    public void Csv_writes_floats_as_python_str_does()
    {
        double[] xs = [0.0, -0.0, 1.0, 1234.5, 0.1 + 0.2, 1e-4, 1e-5, 0.000123456, 1e15, 1e16, 12345678901234567.0, 1.5e300, -2.75, 3.0e-7,
                       100.0, 1234567.891, 9999999999999998.0, 0.5, 2.0 / 3.0, -1e-10, 123456789.0];
        // Python's str(float(x)) for the same values (Golden/capture/csv_floats.py).
        Golden.Equal("CsvFloats", xs.Select(x => new { x, str = ReportExport.PyFloat(x) }));
        string csv = Encoding.UTF8.GetString(ReportExport.BuildCsv(new ReportTable
        {
            Title = "t", Columns = [new("a"), new("b"), new("c")], Rows = [new ReportRow([0.0, 0m, 1.10m])],
        }));
        Assert.Contains("0.0,0,1.10", csv);
    }

    [PerfFact]
    public async Task The_full_year_invoice_register_exports_within_budget()
    {
        // The register's shape (2,760 invoices × 14 columns), read from the speed DB the way a register reads it.
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        DateTime from = Perf.YearStart.ToDateTime(TimeOnly.MinValue), to = Perf.YearEnd.ToDateTime(TimeOnly.MaxValue);
        var invoices = await db.Set<InvInvoice>().AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id)
            .Select(i => new { i.InvoiceNumber, i.InvoiceDate, i.DueDate, Customer = i.Customer != null ? i.Customer.Name : "", i.VoucherStatus,
                               i.Subtotal, i.TotalDiscount, i.TotalTax, i.TotalFurtherTax, i.TotalCharges, i.TotalAmount, i.PaidAmount })
            .ToListAsync();
        var rows = invoices.Select(i => new ReportRow(
        [
            i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.Customer, i.VoucherStatus, i.Subtotal ?? 0m, i.TotalDiscount ?? 0m, i.TotalTax ?? 0m,
            i.TotalFurtherTax ?? 0m, i.TotalCharges ?? 0m, i.TotalAmount ?? 0m, i.PaidAmount ?? 0m, 0m, (i.TotalAmount ?? 0m) - (i.PaidAmount ?? 0m),
        ])).ToList();
        rows.Add(new ReportRow(["", "", "", $"Total ({invoices.Count})", "", .. Enumerable.Repeat<object?>(0m, 9)], "grand"));
        var t = new ReportTable
        {
            Title = "Sales Invoice Register", Period = "FY 2025-2026", Rows = rows,
            Columns = [new("Number"), new("Date", ReportColumnKind.Date), new("Due", ReportColumnKind.Date), new("Customer"), new("Status"),
                       .. new[] { "Subtotal", "Discount", "Tax", "Further tax", "Charges", "Total", "Received", "Returned", "Balance" }
                           .Select(h => new ReportColumn(h, ReportColumnKind.Money))],
        };
        var x = Exporter();
        var (excel, _) = Time(() => x.BuildExcel(t));
        var (pdf, _) = Time(() => x.BuildPdf(t));
        var (csv, _) = Time(() => ReportExport.BuildCsv(t));
        output.WriteLine($"Register {invoices.Count} rows × 14: Excel {excel.TotalMilliseconds:F0} ms, PDF {pdf.TotalMilliseconds:F0} ms, CSV {csv.TotalMilliseconds:F0} ms");
        Assert.True(invoices.Count > 2000);
        Assert.True(excel.TotalMilliseconds <= 1000, $"Excel {excel.TotalMilliseconds:F0} ms");
        Assert.True(pdf.TotalMilliseconds <= 3000, $"PDF {pdf.TotalMilliseconds:F0} ms");
    }
}
