namespace Accountix.Parties.Services;

/// <summary>One invoice of the register (registers.py _sales_rows / _purchase_rows).</summary>
public sealed record RegisterInvoiceRow(int Id, DateTime? Date, string InvoiceNumber, string VoucherNumber, string Party,
                                        decimal[] Vals, string Status, string StatusClass);

/// <summary>A chunk of the register and the totals of the whole filtered set (_register_data).</summary>
public sealed record RegisterData(string Doc, string Title, IReadOnlyList<string> Columns, List<RegisterInvoiceRow> Rows,
                                  decimal[] Totals, int TotalCount);

/// <summary>
/// The sales / purchase invoice register (invoicing_app/routes/registers.py). Totals are SQL sums over the
/// whole filter; rows are read a chunk at a time; "Returned" is the approved credit / debit notes against
/// each invoice, so balance = total − paid − returned.
/// </summary>
public sealed class InvoiceRegister(AccountixDbContext db, PaymentTracking tracking)
{
    public static readonly string[] SalesColumns =
        ["Date", "Invoice #", "Voucher #", "Customer", "Subtotal", "Discount", "Sales Tax", "Further Tax", "WHT", "Total",
         "Paid", "Returned", "Balance", "Status"];
    public static readonly string[] PurchaseColumns =
        ["Date", "Invoice #", "Voucher #", "Supplier", "Subtotal", "Discount", "Expenses", "Tax", "WHT", "Net Payable",
         "Paid", "Returned", "Balance", "Status"];
    public const int NVals = 9;

    public static string TitleOf(string doc) => doc == "purchase" ? "Purchase Invoice Register" : "Sales Invoice Register";
    public static string[] ColumnsOf(string doc) => doc == "purchase" ? PurchaseColumns : SalesColumns;

    public IQueryable<InvInvoice> SalesQuery(string status, DateOnly? from, DateOnly? to)
    {
        var q = db.Set<InvInvoice>().AsNoTracking();
        if (status == "approved") q = q.Where(i => i.VoucherStatus == "approved");
        else if (status == "unapproved") q = q.Where(i => i.VoucherStatus == null || i.VoucherStatus != "approved");
        var (start, end) = PeriodFilters.DayBounds(from, to);
        if (start is DateTime s) q = q.Where(i => i.InvoiceDate >= s);
        if (end is DateTime e) q = q.Where(i => i.InvoiceDate <= e);
        return q;
    }

    public IQueryable<InvPurchaseInvoice> PurchaseQuery(string status, DateOnly? from, DateOnly? to)
    {
        var q = db.Set<InvPurchaseInvoice>().AsNoTracking();
        if (status == "approved") q = q.Where(i => i.Status == "approved");
        else if (status == "unapproved") q = q.Where(i => i.Status == null || i.Status != "approved");
        var (start, end) = PeriodFilters.DayBounds(from, to);
        if (start is DateTime s) q = q.Where(i => i.InvoiceDate >= s);
        if (end is DateTime e) q = q.Where(i => i.InvoiceDate <= e);
        return q;
    }

    /// <summary>_register_data: the requested chunk (everything when limit is null) and full-set totals.</summary>
    public async Task<RegisterData> DataAsync(string doc, string status, DateOnly? from, DateOnly? to, int? limit = null, int offset = 0,
                                              int? knownTotal = null, CancellationToken ct = default)
    {
        if (doc == "purchase")
        {
            var q = PurchaseQuery(status, from, to);
            var sums = await q.GroupBy(_ => 1).Select(g => new
            {
                N = g.Count(), Sub = g.Sum(i => i.Subtotal ?? 0), Disc = g.Sum(i => i.TotalDiscount ?? 0), Exp = g.Sum(i => i.TotalExpenses ?? 0),
                Tax = g.Sum(i => i.TotalTax ?? 0), Wht = g.Sum(i => i.TotalWithholdingTax ?? 0),
                Net = g.Sum(i => i.NetPayable ?? i.TotalAmount ?? 0), Paid = g.Sum(i => i.PaidAmount ?? 0),
            }).FirstOrDefaultAsync(ct);
            int count = knownTotal ?? sums?.N ?? 0;
            var ids = q.Select(i => i.Id);
            decimal back = await db.Set<InvPurchaseReturn>().AsNoTracking()
                .Where(r => r.Status == "approved" && ids.Contains(r.OriginalInvoiceId)).SumAsync(r => r.NetReturnAmount ?? 0, ct);
            var ordered = q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).Select(i => new
            {
                i.Id, i.InvoiceDate, i.InvoiceNumber, i.VoucherNumber, i.SupplierId, i.Subtotal, i.TotalDiscount, i.TotalExpenses,
                i.TotalTax, i.TotalWithholdingTax, i.NetPayable, i.TotalAmount, i.PaidAmount, i.Status,
            });
            var page = await (limit is int l ? ordered.Skip(offset).Take(l) : ordered).ToListAsync(ct);
            var returned = await tracking.CreditedByDocAsync(TrackingRules.Purchase, page.Select(p => p.Id).ToList(), ct);
            var parties = await tracking.SuppliersAsync(ct);
            var rows = page.Select(i =>
            {
                decimal net = i.NetPayable ?? i.TotalAmount ?? 0, paid = i.PaidAmount ?? 0, rb = returned.GetValueOrDefault(i.Id);
                bool approved = (i.Status ?? "") == "approved";
                return new RegisterInvoiceRow(i.Id, i.InvoiceDate, i.InvoiceNumber, i.VoucherNumber,
                    parties.TryGetValue(i.SupplierId, out var p) ? p.Name : "-",
                    [i.Subtotal ?? 0, i.TotalDiscount ?? 0, i.TotalExpenses ?? 0, i.TotalTax ?? 0, i.TotalWithholdingTax ?? 0, net, paid, rb, net - paid - rb],
                    approved ? "Approved" : "Unapproved", approved ? "approved" : "unapproved");
            }).ToList();
            decimal sNet = sums?.Net ?? 0, sPaid = sums?.Paid ?? 0;
            decimal[] totals = [sums?.Sub ?? 0, sums?.Disc ?? 0, sums?.Exp ?? 0, sums?.Tax ?? 0, sums?.Wht ?? 0, sNet, sPaid, back, sNet - sPaid - back];
            return new RegisterData("purchase", TitleOf("purchase"), PurchaseColumns, rows, totals, count);
        }
        else
        {
            var q = SalesQuery(status, from, to);
            var sums = await q.GroupBy(_ => 1).Select(g => new
            {
                N = g.Count(), Sub = g.Sum(i => i.Subtotal ?? 0), Disc = g.Sum(i => i.TotalDiscount ?? 0), Tax = g.Sum(i => i.TotalTax ?? 0),
                Ft = g.Sum(i => i.TotalFurtherTax ?? 0), Wht = g.Sum(i => i.TotalWithholdingTax ?? 0),
                Total = g.Sum(i => i.TotalAmount ?? 0), Paid = g.Sum(i => i.PaidAmount ?? 0),
            }).FirstOrDefaultAsync(ct);
            int count = knownTotal ?? sums?.N ?? 0;
            var ids = q.Select(i => i.Id);
            decimal back = await db.Set<InvSalesReturn>().AsNoTracking()
                .Where(r => r.Status == "approved" && ids.Contains(r.OriginalInvoiceId)).SumAsync(r => r.NetReturnAmount ?? 0, ct);
            var ordered = q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).Select(i => new
            {
                i.Id, i.InvoiceDate, i.InvoiceNumber, i.VoucherNumber, i.CustomerId, i.Subtotal, i.TotalDiscount, i.TotalTax,
                i.TotalFurtherTax, i.TotalWithholdingTax, i.TotalAmount, i.PaidAmount, i.VoucherStatus,
            });
            var page = await (limit is int l ? ordered.Skip(offset).Take(l) : ordered).ToListAsync(ct);
            var returned = await tracking.CreditedByDocAsync(TrackingRules.Sales, page.Select(p => p.Id).ToList(), ct);
            var parties = await tracking.CustomersAsync(ct);
            var rows = page.Select(i =>
            {
                decimal total = i.TotalAmount ?? 0, paid = i.PaidAmount ?? 0, rb = returned.GetValueOrDefault(i.Id);
                bool approved = (i.VoucherStatus ?? "") == "approved";
                return new RegisterInvoiceRow(i.Id, i.InvoiceDate, i.InvoiceNumber, i.VoucherNumber,
                    parties.TryGetValue(i.CustomerId, out var p) ? p.Name : "-",
                    [i.Subtotal ?? 0, i.TotalDiscount ?? 0, i.TotalTax ?? 0, i.TotalFurtherTax ?? 0, i.TotalWithholdingTax ?? 0, total, paid, rb, total - paid - rb],
                    approved ? "Approved" : "Unapproved", approved ? "approved" : "unapproved");
            }).ToList();
            decimal sTotal = sums?.Total ?? 0, sPaid = sums?.Paid ?? 0;
            decimal[] totals = [sums?.Sub ?? 0, sums?.Disc ?? 0, sums?.Tax ?? 0, sums?.Ft ?? 0, sums?.Wht ?? 0, sTotal, sPaid, back, sTotal - sPaid - back];
            return new RegisterData("sales", TitleOf("sales"), SalesColumns, rows, totals, count);
        }
    }
}
