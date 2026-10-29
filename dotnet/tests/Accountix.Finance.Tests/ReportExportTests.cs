using System.Net;
using System.Text;
using Accountix.Core.Shared;
using Accountix.Finance.Services;
using Accountix.TestSupport;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;

namespace Accountix.Finance.Tests;

public class ReportExportTests
{
    private static ReportExport Exporter() => new(new Microsoft.AspNetCore.Http.HttpContextAccessor(),
        new CompanyClock(null!, new Accountix.Core.Data.CompanyContext(), TimeProvider.System));

    private static ReportTable Sample(int rows = 3) => new()
    {
        Title = "Trial Balance",
        Period = "01 Jul 2025 – 30 Jun 2026",
        Filters = [("", "Labels: All")],
        Columns = [new("Code"), new("Account"), new("Debit", ReportColumnKind.Money), new("Credit", ReportColumnKind.Money)],
        Rows = Enumerable.Range(1, rows).Select(i => new ReportRow([$"1-01-0{i}", $"Account {i} & Co", 1234.5m * i, 0m], "account"))
            .Append(new ReportRow(["", "Total", 1234.5m * rows * (rows + 1) / 2, 0m], "grand")).ToList(),
        MonoColumn = 0,
    };

    [Fact]
    public void Excel_has_the_heading_header_row_money_format_and_a_frozen_header()
    {
        byte[] bytes = Exporter().BuildExcel(Sample());
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);
        Assert.Equal("Trial Balance", ws.Name);
        Assert.Equal("Trial Balance", ws.Cell(2, 1).GetString());
        Assert.Equal("01 Jul 2025 – 30 Jun 2026", ws.Cell(3, 1).GetString());
        Assert.StartsWith("Labels: All  ·  Generated ", ws.Cell(4, 1).GetString());
        Assert.Equal("Debit", ws.Cell(5, 3).GetString());
        Assert.Equal(1234.5, ws.Cell(6, 3).GetDouble());
        Assert.Equal(Money.ExcelWestern, ws.Cell(6, 3).Style.NumberFormat.Format);
        Assert.Equal(5, ws.SheetView.SplitRow);
        Assert.Equal(XLPageOrientation.Portrait, ws.PageSetup.PageOrientation);
        Assert.True(ws.Cell(9, 1).Style.Fill.BackgroundColor.Color.ToArgb() != 0);   // grand row filled
    }

    [Fact]
    public void Pdf_renders_pages_with_a_repeating_header()
    {
        byte[] bytes = Exporter().BuildPdf(Sample(rows: 120));
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_DUMP_DIR") is { Length: > 0 } dump)
            File.WriteAllBytes(Path.Combine(dump, "sample.pdf"), bytes);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 2);
    }

    [Fact]
    public void Csv_has_a_bom_raw_numbers_and_quoted_text()
    {
        string csv = Encoding.UTF8.GetString(ReportExport.BuildCsv(Sample(1)));
        Assert.StartsWith("﻿Code,Account,Debit,Credit\r\n", csv);
        Assert.Contains("1-01-01,Account 1 & Co,1234.5,0", csv);
    }

    [Fact]
    public void File_names_read_company_report_period()
    {
        Assert.Equal("Trial_Balance_2026-06-30.xlsx", Exporter().FileName("Trial Balance", "xlsx", "2026-06-30"));
        Assert.Equal("PandL_by_Label.pdf", Exporter().FileName("P&L by Label", "pdf", null));   // Python's _slug
    }

    [ReferenceFact]
    public async Task The_voucher_register_exports_through_the_pipeline()
    {
        await using var host = await ModuleHost.StartAsync(typeof(FinanceModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        var c = host.ClientFor(Reference.UserId(Reference.Email("owner_b")), Reference.Company("b"));
        foreach (string fmt in new[] { "excel", "pdf", "csv" })
        {
            var r = await c.GetAsync($"/accounting/registers/vouchers/export?mode=month&month=2026-04&fmt={fmt}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("Voucher_Register_April_2026", r.Content.Headers.ContentDisposition!.FileNameStar ?? r.Content.Headers.ContentDisposition.FileName);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/accounting/registers/vouchers/export?fmt=doc")).StatusCode);
    }
}
