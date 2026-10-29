using ClosedXML.Excel;

namespace Accountix.Inventory.Services;

/// <summary>The import templates (openpyxl in Python): one sheet, a heading row and sample rows, all text.</summary>
public static class ExcelSheets
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Template(string title, string[] headers, string[][] samples, double[] widths)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(title);
        for (int c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        for (int r = 0; r < samples.Length; r++)
            for (int c = 0; c < samples[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = samples[r][c];      // text cells, as ws.append() of strings
        for (int c = 0; c < widths.Length; c++)
            ws.Column(c + 1).Width = widths[c];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
