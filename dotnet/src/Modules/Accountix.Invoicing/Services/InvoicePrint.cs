using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Accountix.Invoicing.Services;

/// <summary>
/// Print templates (shared/models/invoice_template.py): the options a stored
/// template carries, the totals block built at print time, and the
/// {{placeholder}} renderer. The template bodies themselves are built by the
/// Settings pages; printing only reads them.
/// </summary>
public static class InvoiceTemplates
{
    private static readonly string[] Header = ["show_logo", "show_company_tax_id", "show_status", "show_due_date"];
    private static readonly string[] Party = ["show_party_address", "show_party_contact", "show_party_tax_id"];
    private static readonly string[] CommonTotals = ["show_discount", "show_tax"];
    private static readonly string[] SalesTotals = ["show_delivery", "show_installation", "show_bank_details"];
    private static readonly string[] PurchaseTotals = ["show_commission", "show_freight", "show_loading", "show_withholding"];
    private static readonly string[] DisplayModes = ["discount_display", "tax_display", "charges_display"];
    private static readonly string[] Footer = ["show_notes", "show_signature", "show_thanks"];

    /// <summary>default_options(doc_type): every toggle on except the status stamp; display modes "combined".</summary>
    public static Dictionary<string, object> DefaultOptions(string docType)
    {
        var o = new Dictionary<string, object>();
        foreach (var k in Header.Concat(Party).Concat(CommonTotals).Concat(docType == "sales" ? SalesTotals : PurchaseTotals)
                     .Concat(DisplayModes).Concat(Footer))
            o[k] = true;
        o["show_status"] = false;
        foreach (var k in DisplayModes) o[k] = "combined";
        return o;
    }

    /// <summary>normalise_options: the stored JSON coerced to the full known set.</summary>
    public static Dictionary<string, object> Options(string docType, string? raw)
    {
        var known = DefaultOptions(docType);
        if (string.IsNullOrEmpty(raw)) return known;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return known;
        }
        if (root.ValueKind != JsonValueKind.Object) return known;
        var outD = new Dictionary<string, object>();
        foreach (var (k, v) in known)
        {
            bool has = root.TryGetProperty(k, out var val);
            if (DisplayModes.Contains(k))
                outD[k] = has && val.ValueKind == JsonValueKind.String && val.GetString() is "per_line" or "combined"
                    ? val.GetString()! : v;
            else
                outD[k] = has ? PyData.Truthy(val) : (bool)v;
        }
        return outD;
    }

    public static bool On(IReadOnlyDictionary<string, object> o, string key) => o.TryGetValue(key, out var v) && v is true;
    public static string? Str(IReadOnlyDictionary<string, object> o, string key) => o.TryGetValue(key, out var v) ? v as string : null;

    /// <summary>items_table_metrics(n_cols): starting font size and cell padding.</summary>
    public static (double Font, string Pad) ItemsTableMetrics(int nCols)
    {
        int n = Math.Max(1, nCols);
        int over = Math.Max(0, n - 10);
        double font = Math.Max(7.0, 11.0 - 0.55 * over);
        string pad = over == 0 ? "0.5em 0.7em" : over <= 3 ? "0.45em 0.55em" : over <= 5 ? "0.4em 0.45em" : "0.35em 0.4em";
        return (Py.Round(font, 1), pad);
    }

    private static string TotalsRows(string docType, IReadOnlyDictionary<string, object> opts, string accent)
    {
        var rows = new List<(string Text, string Token, bool Total)> { ("Subtotal", "{{subtotal}}", false) };
        if (On(opts, "show_discount") && Str(opts, "discount_display") != "per_line") rows.Add(("Discount", "{{discount}}", false));
        if (On(opts, "show_tax") && Str(opts, "tax_display") != "per_line") rows.Add(("Sales Tax", "{{tax}}", false));
        bool chargesCombined = Str(opts, "charges_display") != "per_line";
        if (docType == "sales")
        {
            if (On(opts, "show_delivery") && chargesCombined) rows.Add(("Delivery", "{{delivery_charges}}", false));
            if (On(opts, "show_installation") && chargesCombined) rows.Add(("Installation", "{{installation_charges}}", false));
        }
        else
        {
            if (On(opts, "show_commission") && chargesCombined) rows.Add(("Commission", "{{commission}}", false));
            if (On(opts, "show_freight") && chargesCombined) rows.Add(("Freight", "{{freight}}", false));
            if (On(opts, "show_loading") && chargesCombined) rows.Add(("Loading / Unloading", "{{loading_unloading}}", false));
            if (On(opts, "show_withholding") && chargesCombined) rows.Add(("Withholding Tax", "{{withholding_tax}}", false));
        }
        rows.Add((docType == "sales" ? "Net Receivable" : "Net Payable", "{{grand_total}}", true));
        var outL = new List<string>();
        foreach (var (text, token, isTotal) in rows)
        {
            if (isTotal)
                outL.Add($"<tr><td style=\"padding:10px 12px;text-align:right;font-weight:700;font-size:15px;border-top:2px solid {accent};\">{text}</td>" +
                         $"<td style=\"padding:10px 12px;text-align:right;font-weight:800;font-size:15px;border-top:2px solid {accent};color:{accent};" +
                         $"white-space:nowrap;\">{token}</td></tr>");
            else
                outL.Add($"<tr><td style=\"padding:5px 12px;text-align:right;color:#475569;\">{text}</td>" +
                         $"<td style=\"padding:5px 12px;text-align:right;font-weight:600;white-space:nowrap;\">{token}</td></tr>");
        }
        return string.Join("\n      ", outL);
    }

    private static string BankBlock(string docType, IReadOnlyDictionary<string, object> opts, string accent)
    {
        if (docType != "sales" || !On(opts, "show_bank_details")) return "";
        string label = $"font-size:10px;font-weight:700;letter-spacing:1px;color:{accent};text-transform:uppercase;";
        const string row = "padding:1px 0;color:#475569;font-size:11.5px;";
        const string key = "padding:1px 10px 1px 0;color:#94a3b8;font-size:11.5px;white-space:nowrap;";
        return "<div style=\"max-width:82mm;\">" +
               $"<div style=\"{label}\">Payment Details</div>" +
               "<table style=\"border-collapse:collapse;margin-top:5px;\">" +
               $"<tr><td style=\"{key}\">Bank</td><td style=\"{row}font-weight:600;color:#0f172a;\">{{{{company_bank_name}}}}</td></tr>" +
               $"<tr><td style=\"{key}\">Title</td><td style=\"{row}\">{{{{company_bank_account_title}}}}</td></tr>" +
               $"<tr><td style=\"{key}\">Account #</td><td style=\"{row}\">{{{{company_bank_account_number}}}}</td></tr>" +
               "</table></div>";
    }

    private const string TableClose = "\n</table></td></tr></table>";

    private static string TableOpen(string gap) =>
        "<table class=\"inv-totals\" style=\"width:100%;border-collapse:collapse;margin-top:14px;\"><tr>" +
        $"<td class=\"inv-totals-gap\" style=\"vertical-align:top;\">{gap}</td>" +
        "<td class=\"inv-totals-box\" style=\"width:72mm;vertical-align:top;\">" +
        "<table style=\"width:100%;border-collapse:collapse;\">\n";

    /// <summary>build_totals_table(doc_type, opts, accent).</summary>
    public static string BuildTotalsTable(string docType, IReadOnlyDictionary<string, object> opts, string accent) =>
        TableOpen(BankBlock(docType, opts, accent)) + TotalsRows(docType, opts, accent) + TableClose;

    /// <summary>render_invoice_template: replace {{key}} tokens until nothing more resolves (at most 5 passes).</summary>
    public static string Render(string body, IReadOnlyList<KeyValuePair<string, string?>> ctx)
    {
        for (int pass = 0; pass < 5; pass++)
        {
            string before = body;
            foreach (var (key, val) in ctx)
            {
                string token = "{{" + key + "}}";
                if (body.Contains(token, StringComparison.Ordinal))
                    body = body.Replace(token, val ?? "", StringComparison.Ordinal);
            }
            if (body == before) break;
        }
        return body;
    }
}

/// <summary>
/// The single-document print HTML for sales and purchase invoices
/// (shared/invoice_print.py): the form's print and the bulk register print
/// call the same functions, so the two can never diverge.
/// </summary>
public sealed class InvoicePrint(AccountixDbContext db, DocSettings settings)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>html.escape(str(value), quote=True).</summary>
    public static string E(string? value) => value is null ? "" :
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");

    /// <summary>The template the company chose for the document type ("sales" | "purchase"), else its default.</summary>
    public async Task<InvoiceTemplate?> TemplateForAsync(string docType, CancellationToken ct)
    {
        var rs = await settings.ReportAsync(ct);
        int? tid = docType == "purchase" ? rs.PurchaseTemplateId : rs.SalesTemplateId;
        InvoiceTemplate? t = null;
        if (tid is int id)
            t = await db.Set<InvoiceTemplate>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        t ??= await db.Set<InvoiceTemplate>().AsNoTracking().Where(x => x.Type == docType && x.IsDefault == true)
                  .OrderBy(x => x.Id).FirstOrDefaultAsync(ct)
              ?? await db.Set<InvoiceTemplate>().AsNoTracking().Where(x => x.Type == docType).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        return t;
    }

    private async Task<CompanyInfo> CompanyAsync(CancellationToken ct) =>
        await db.Set<CompanyInfo>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct) ?? new CompanyInfo();

    private static string Money(double v, string? nf) => Py.Amount(v, 2, nf ?? "en");

    private static string Date(DateTime? d) => d?.ToString("dd-MMM-yyyy", Inv) ?? "";

    /// <summary>f"{x}" of a Money value (a float), or "None".</summary>
    private static string PyStr(decimal? d) => d is null ? "None" : Py.Repr(Py.F(d));

    private static string Fallback(string kind, string? voucherNumber, DateTime? date, decimal? total, decimal? paid,
                                   string partyName, IEnumerable<(string? Desc, decimal? Qty, decimal? Price, double Amount)> items,
                                   CompanyInfo company, string? nf)
    {
        var rows = new StringBuilder();
        int i = 0;
        foreach (var it in items)
        {
            i++;
            rows.Append($"<tr><td>{i}</td><td>{E(it.Desc)}</td>")
                .Append($"<td style='text-align:right'>{(it.Qty is decimal q && q != 0 ? Py.Repr(Py.F(q)) : "0")}</td>")
                .Append($"<td style='text-align:right'>{Money(Py.F(it.Price), nf)}</td>")
                .Append($"<td style='text-align:right'>{Money(it.Amount, nf)}</td></tr>");
        }
        double t = Py.F(total), p = Py.F(paid);
        return "<div style='font-family:Inter,Arial,sans-serif;max-width:750px;margin:0 auto;padding:24px;'>" +
               $"<h2 style='text-align:center;margin:0'>{E(company.CompanyName)}</h2>" +
               $"<h3 style='text-align:center;margin:6px 0 16px'>{E(kind)} — {E(voucherNumber)}</h3>" +
               $"<p><strong>{E(partyName)}:</strong> {Date(date)}</p>" +
               "<table style='width:100%;border-collapse:collapse;font-size:12px'>" +
               "<thead><tr><th>#</th><th>Description</th><th style='text-align:right'>Qty</th><th style='text-align:right'>Rate</th>" +
               "<th style='text-align:right'>Amount</th></tr></thead>" +
               $"<tbody>{rows}</tbody>" +
               "<tfoot><tr><td colspan='4' style='text-align:right'><strong>Total:</strong></td>" +
               $"<td style='text-align:right'><strong>{Money(t, nf)}</strong></td></tr>" +
               $"<tr><td colspan='4' style='text-align:right'>Paid:</td><td style='text-align:right'>{Money(p, nf)}</td></tr>" +
               $"<tr><td colspan='4' style='text-align:right'>Balance:</td><td style='text-align:right'>{Money(t - p, nf)}</td></tr>" +
               "</tfoot></table></div>";
    }

    private static double FallbackAmount(decimal? after, decimal? before, decimal? qty, decimal? price)
    {
        double a = Py.F(after);
        if (a != 0) return a;
        double b = Py.F(before);
        if (b != 0) return b;
        return Py.F(qty) * Py.F(price);
    }

    private static List<KeyValuePair<string, string?>> CompanyCtx(CompanyInfo c) =>
    [
        new("company_logo", !string.IsNullOrEmpty(c.LogoUrl) ? $"<img src=\"{E(c.LogoUrl)}\" style=\"max-height:60px;\" alt=\"Logo\">" : ""),
        new("company_name", E(c.CompanyName)),
        new("company_address", E(c.Address)),
        new("company_city", E(c.City)),
        new("company_phone", E(c.Phone)),
        new("company_email", E(c.Email)),
        new("company_tax_id", E(c.TaxId)),
        new("company_bank_name", E(c.BankName)),
        new("company_bank_account_title", E(c.BankAccountTitle)),
        new("company_bank_account_number", E(c.BankAccountNumber)),
    ];

    private static List<KeyValuePair<string, string?>> PartyCtx(string? name, string? address, string? city, string? phone, string? email, string? taxId) =>
    [
        new("party_name", name is not null ? E(name) : ""),
        new("party_address", !string.IsNullOrEmpty(address) ? E(address) : ""),
        new("party_city", !string.IsNullOrEmpty(city) ? E(city) : ""),
        new("party_phone", !string.IsNullOrEmpty(phone) ? E(phone) : ""),
        new("party_email", !string.IsNullOrEmpty(email) ? E(email) : ""),
        new("party_tax_id", !string.IsNullOrEmpty(taxId) ? E(taxId) : ""),
    ];

    /// <summary>
    /// render_sales_print_html: the full print for one sales invoice. Loads the items, products and
    /// charges itself unless the bulk caller hands them in (all three, already loaded).
    /// </summary>
    public async Task<string> SalesAsync(InvInvoice invoice, CancellationToken ct, IReadOnlyList<InvInvoiceItem>? items = null,
                                         IReadOnlyDictionary<int, InvProduct>? products = null, IReadOnlyList<AdditionalCharge>? charges = null,
                                         InvoiceTemplate? template = null, bool templateGiven = false, CompanyInfo? company = null,
                                         string? numberFormat = null)
    {
        var tpl = templateGiven ? template : await TemplateForAsync("sales", ct);
        company ??= await CompanyAsync(ct);
        string? nf = numberFormat ?? company.NumberFormat ?? "en";
        items ??= await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoice.Id).OrderBy(i => i.Id).ToListAsync(ct);
        if (products is null)
        {
            var pids = items.Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).Distinct().ToList();
            products = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        }
        var customer = invoice.Customer ?? await db.Set<InvCustomer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, ct);
        if (tpl is null)
            return Fallback("Sales Invoice", invoice.VoucherNumber, invoice.InvoiceDate, invoice.TotalAmount, invoice.PaidAmount,
                customer?.Name ?? "", items.Select(it => (it.Description, it.Quantity, it.UnitPrice,
                    FallbackAmount(it.TotalAfterDiscount, it.TotalBeforeDiscount, it.Quantity, it.UnitPrice))), company, nf);
        charges ??= await db.Set<AdditionalCharge>().AsNoTracking().Where(c => c.DocType == "SI" && c.DocId == invoice.Id).OrderBy(c => c.Id).ToListAsync(ct);

        var topts = InvoiceTemplates.Options(tpl.Type, tpl.OptionsJson);
        bool showDisc = invoice.DiscountMode == "individual" && InvoiceTemplates.Str(topts, "discount_display") == "per_line";
        bool showTax = invoice.TaxMode == "individual" && InvoiceTemplates.Str(topts, "tax_display") == "per_line";
        bool showChg = invoice.ChargesMode == "individual" && InvoiceTemplates.Str(topts, "charges_display") == "per_line";
        int nCols = 10 + (showDisc ? 2 : 0) + (showTax ? 2 : 0) + (showChg ? 2 : 0);
        var (fs, pad) = InvoiceTemplates.ItemsTableMetrics(nCols);
        string tds = $"padding:{pad};border:1px solid #e2e8f0;white-space:nowrap;";
        string tdc = tds + "text-align:center;", tdr = tds + "text-align:right;";

        double totQty = 0, totDisc = 0, totExcl = 0, totDel = 0, totInst = 0, totTax = 0, totIncl = 0, totLine = 0;
        bool qtyIsFloat = false;
        var rows = new StringBuilder();
        int i = 0;
        foreach (var it in items)
        {
            i++;
            products.TryGetValue(it.ProductId ?? 0, out var product);
            double da = Py.F(it.DiscountAmount), tp = Py.F(it.SalesTaxPct), lineTotal = Py.F(it.TotalBeforeDiscount);
            double amtExcl = lineTotal - da, taxAmt = amtExcl * tp / 100, amtIncl = amtExcl + taxAmt;
            if (Py.F(it.Quantity) != 0) qtyIsFloat = true;
            totQty += Py.F(it.Quantity); totDisc += da; totExcl += amtExcl; totDel += Py.F(it.Delivery); totInst += Py.F(it.Installation);
            totTax += taxAmt; totIncl += amtIncl; totLine += lineTotal;
            var c = new StringBuilder();
            c.Append($"<td style='{tdc}'>{i}</td>")
             .Append($"<td style='{tds}'>{(product is not null ? E(product.Sku) : "")}</td>")
             .Append($"<td class='inv-desc' style='{tds}white-space:normal;'>{E(it.Description)}</td>")
             .Append($"<td style='{tdc}'>{PyStr(it.Quantity)}</td>")
             .Append($"<td style='{tdc}'>{E(it.Unit)}</td>")
             .Append($"<td style='{tdr}'>{Money(Py.F(it.UnitPrice), nf)}</td>");
            if (showDisc)
            {
                c.Append($"<td style='{tdr}'>{Py.Fixed(Py.F(it.DiscountPct), 1)}%</td>");
                c.Append($"<td style='{tdr}'>{Money(da, nf)}</td>");
            }
            c.Append($"<td style='{tdr}'>{Money(amtExcl, nf)}</td>");
            if (showTax)
            {
                double q = Py.F(it.Quantity);
                double puTax = taxAmt / (q != 0 ? q : 1);
                c.Append($"<td style='{tdr}'>{Py.Fixed(tp, 1)}%</td>");
                c.Append($"<td style='{tdr}'>{Money(puTax, nf)}</td>");
            }
            if (showChg)
            {
                c.Append($"<td style='{tdr}'>{Money(Py.F(it.Delivery), nf)}</td>");
                c.Append($"<td style='{tdr}'>{Money(Py.F(it.Installation), nf)}</td>");
            }
            c.Append($"<td style='{tdr}'>{Money(taxAmt, nf)}</td>");
            c.Append($"<td style='{tdr}'>{Money(amtIncl, nf)}</td>");
            c.Append($"<td style='{tdr}'>{Money(lineTotal, nf)}</td>");
            rows.Append("<tr>").Append(c).Append("</tr>");
        }
        string tdsB = $"padding:{pad};border:1px solid #e2e8f0;font-weight:700;background:#f1f5f9;white-space:nowrap;";
        string tdrB = tdsB + "text-align:right;";
        var foot = new StringBuilder();
        foot.Append($"<td style='{tdsB};text-align:center;'></td>")
            .Append($"<td style='{tdsB}'></td>")
            .Append($"<td class='inv-desc' style='{tdsB}'>Total</td>")
            .Append($"<td style='{tdsB};text-align:center;'>{(qtyIsFloat ? Py.Repr(totQty) : "0")}</td>")
            .Append($"<td style='{tdsB}'></td>")
            .Append($"<td style='{tdrB}'></td>");
        if (showDisc) { foot.Append($"<td style='{tdrB}'></td>"); foot.Append($"<td style='{tdrB}'>{Money(totDisc, nf)}</td>"); }
        foot.Append($"<td style='{tdrB}'>{Money(totExcl, nf)}</td>");
        if (showTax) { foot.Append($"<td style='{tdrB}'></td>"); foot.Append($"<td style='{tdrB}'>{Money(totTax, nf)}</td>"); }
        if (showChg) { foot.Append($"<td style='{tdrB}'>{Money(totDel, nf)}</td>"); foot.Append($"<td style='{tdrB}'>{Money(totInst, nf)}</td>"); }
        foot.Append($"<td style='{tdrB}'>{Money(totTax, nf)}</td>");
        foot.Append($"<td style='{tdrB}'>{Money(totIncl, nf)}</td>");
        foot.Append($"<td style='{tdrB}'>{Money(totLine, nf)}</td>");

        string hd = $"padding:{pad};border:1px solid #1e293b;white-space:normal;text-align:center;vertical-align:middle;";
        var head = new StringBuilder();
        head.Append($"<th style='{hd}'>#</th><th style='{hd}'>SKU</th><th class='inv-desc' style='{hd}'>Description</th>")
            .Append($"<th style='{hd}'>Qty</th><th style='{hd}'>Unit</th><th style='{hd}'>Per Unit Price</th>");
        if (showDisc) head.Append($"<th style='{hd}'>Discount %</th><th style='{hd}'>Discount allowed</th>");
        head.Append($"<th style='{hd}'>Amount Excl. of Sales Tax</th>");
        if (showTax) head.Append($"<th style='{hd}'>Sales Tax %</th><th style='{hd}'>Sales Tax Amount per Unit</th>");
        if (showChg) head.Append($"<th style='{hd}'>Carriage Expense</th><th style='{hd}'>Installation</th>");
        head.Append($"<th style='{hd}'>Total Sales Tax</th><th style='{hd}'>Amount Incl. of Sales Tax</th><th style='{hd}'>Total</th>");

        string itemsTable = $"<table class=\"inv-items\" style=\"width:100%;border-collapse:collapse;font-size:{Py.Repr(fs)}px;\">" +
                            "<thead><tr style=\"background:#1e293b;color:#fff;\">" + head + "</tr></thead><tbody>" + rows +
                            "<tr>" + foot + "</tr></tbody></table>";

        var totals = InvoiceTotals.Sales(TDoc.From(invoice), charges.Select(TCharge.From));
        var display = new Dictionary<string, object>(topts);
        if (invoice.DiscountMode == "individual" && InvoiceTemplates.Str(topts, "discount_display") == "per_line") display["show_discount"] = false;
        if (invoice.TaxMode == "individual" && InvoiceTemplates.Str(topts, "tax_display") == "per_line") display["show_tax"] = false;
        if (invoice.ChargesMode == "individual" && InvoiceTemplates.Str(topts, "charges_display") == "per_line")
        {
            display["show_delivery"] = false;
            display["show_installation"] = false;
        }
        var ctx = CompanyCtx(company);
        ctx.AddRange(
        [
            new("invoice_no", E(invoice.VoucherNumber)),
            new("invoice_date", Date(invoice.InvoiceDate)),
            new("due_date", Date(invoice.DueDate)),
            new("status", invoice.ApprovedAt is not null ? "Approved" : "Unapproved"),
        ]);
        ctx.AddRange(PartyCtx(customer?.Name, customer?.Address, customer?.City, customer?.Phone, customer?.Email, customer?.TaxId));
        ctx.AddRange(
        [
            new("items_table", itemsTable),
            new("totals_table", InvoiceTemplates.BuildTotalsTable("sales", display, string.IsNullOrEmpty(tpl.AccentColor) ? "#0f766e" : tpl.AccentColor)),
            new("subtotal", Money(Py.F(invoice.Subtotal), nf)),
            new("discount", Money(Py.F(invoice.TotalDiscount), nf)),
            new("tax", Money(Py.F(invoice.TotalTax), nf)),
            new("delivery_charges", Money(Py.F(invoice.GlobalDelivery), nf)),
            new("installation_charges", Money(Py.F(invoice.GlobalInstallation), nf)),
            new("grand_total", Money(Py.F(invoice.TotalAmount), nf)),
            new("commission", Money(0, nf)),
            new("freight", Money(0, nf)),
            new("loading_unloading", Money(0, nf)),
            new("taxable_value", Money(totals.SalesTaxBase, nf)),
            new("additional_charges", Money(Py.F(invoice.TotalCharges), nf)),
            new("further_tax", Money(Py.F(invoice.TotalFurtherTax), nf)),
            new("withholding_tax", Money(Py.F(invoice.TotalWithholdingTax), nf)),
            new("notes", E(invoice.Notes)),
        ]);
        return InvoiceTemplates.Render(tpl.BodyHtml, ctx);
    }

    /// <summary>render_purchase_print_html: the full print for one purchase invoice.</summary>
    public async Task<string> PurchaseAsync(InvPurchaseInvoice invoice, CancellationToken ct, IReadOnlyList<InvPurchaseInvoiceItem>? items = null,
                                            IReadOnlyDictionary<int, InvProduct>? products = null, InvoiceTemplate? template = null,
                                            bool templateGiven = false, CompanyInfo? company = null, string? numberFormat = null)
    {
        var tpl = templateGiven ? template : await TemplateForAsync("purchase", ct);
        company ??= await CompanyAsync(ct);
        string? nf = numberFormat ?? company.NumberFormat ?? "en";
        items ??= await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoice.Id).OrderBy(i => i.Id).ToListAsync(ct);
        if (products is null)
        {
            var pids = items.Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).Distinct().ToList();
            products = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        }
        var supplier = invoice.Supplier ?? await db.Set<InvSupplier>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == invoice.SupplierId, ct);
        if (tpl is null)
            return Fallback("Purchase Invoice", invoice.VoucherNumber, invoice.InvoiceDate, invoice.TotalAmount, invoice.PaidAmount,
                supplier?.Name ?? "", items.Select(it => (it.Description, it.Quantity, it.UnitPrice,
                    FallbackAmount(it.TotalAfterDiscount, it.TotalBeforeDiscount, it.Quantity, it.UnitPrice))), company, nf);

        var topts = InvoiceTemplates.Options(tpl.Type, tpl.OptionsJson);
        bool showDisc = invoice.DiscountMode == "individual" && InvoiceTemplates.Str(topts, "discount_display") == "per_line";
        bool showTax = invoice.TaxMode == "individual" && InvoiceTemplates.Str(topts, "tax_display") == "per_line";
        bool showChg = invoice.ExpensesMode == "individual" && InvoiceTemplates.Str(topts, "charges_display") == "per_line";
        int nCols = 10 + (showDisc ? 2 : 0) + (showTax ? 2 : 0) + (showChg ? 3 : 0);
        var (fs, pad) = InvoiceTemplates.ItemsTableMetrics(nCols);
        string tds = $"padding:{pad};border:1px solid #e2e8f0;white-space:nowrap;";
        string tdc = tds + "text-align:center;", tdr = tds + "text-align:right;";

        double totQty = 0, totDisc = 0, totExcl = 0, totComm = 0, totFr = 0, totLd = 0, totTax = 0, totIncl = 0, totLine = 0;
        bool qtyIsFloat = false;
        var rows = new StringBuilder();
        int i = 0;
        foreach (var it in items)
        {
            i++;
            products.TryGetValue(it.ProductId ?? 0, out var product);
            double da = Py.F(it.DiscountAmount), tp = Py.F(it.SalesTaxPct), lineTotal = Py.F(it.TotalBeforeDiscount);
            double amtExcl = lineTotal - da, taxAmt = amtExcl * tp / 100, amtIncl = amtExcl + taxAmt;
            if (Py.F(it.Quantity) != 0) qtyIsFloat = true;
            totQty += Py.F(it.Quantity); totDisc += da; totExcl += amtExcl; totComm += Py.F(it.Commission); totFr += Py.F(it.Freight);
            totLd += Py.F(it.LoadingUnloading); totTax += taxAmt; totIncl += amtIncl; totLine += lineTotal;
            var c = new StringBuilder();
            c.Append($"<td style='{tdc}'>{i}</td>")
             .Append($"<td style='{tds}'>{(product is not null ? E(product.Sku) : "")}</td>")
             .Append($"<td class='inv-desc' style='{tds}white-space:normal;'>{E(it.Description)}</td>")
             .Append($"<td style='{tdc}'>{PyStr(it.Quantity)}</td>")
             .Append($"<td style='{tdc}'>{E(it.Unit)}</td>")
             .Append($"<td style='{tdr}'>{Money(Py.F(it.UnitPrice), nf)}</td>");
            if (showDisc)
            {
                c.Append($"<td style='{tdr}'>{Py.Fixed(Py.F(it.DiscountPct), 1)}%</td>");
                c.Append($"<td style='{tdr}'>{Money(da, nf)}</td>");
            }
            c.Append($"<td style='{tdr}'>{Money(amtExcl, nf)}</td>");
            if (showTax)
            {
                c.Append($"<td style='{tdr}'>{Py.Fixed(tp, 1)}%</td>");
                c.Append($"<td style='{tdr}'>{Money(taxAmt, nf)}</td>");
            }
            if (showChg)
            {
                c.Append($"<td style='{tdr}'>{Money(Py.F(it.Commission), nf)}</td>");
                c.Append($"<td style='{tdr}'>{Money(Py.F(it.Freight), nf)}</td>");
                c.Append($"<td style='{tdr}'>{Money(Py.F(it.LoadingUnloading), nf)}</td>");
            }
            c.Append($"<td style='{tdr}'>{Money(taxAmt, nf)}</td>");
            c.Append($"<td style='{tdr}'>{Money(amtIncl, nf)}</td>");
            c.Append($"<td style='{tdr}'>{Money(lineTotal, nf)}</td>");
            rows.Append("<tr>").Append(c).Append("</tr>");
        }
        string tdsB = $"padding:{pad};border:1px solid #e2e8f0;font-weight:700;background:#f1f5f9;white-space:nowrap;";
        string tdrB = tdsB + "text-align:right;";
        var foot = new StringBuilder();
        foot.Append($"<td style='{tdsB};text-align:center;'></td>")
            .Append($"<td style='{tdsB}'></td>")
            .Append($"<td class='inv-desc' style='{tdsB}'>Total</td>")
            .Append($"<td style='{tdsB};text-align:center;'>{(qtyIsFloat ? Py.Repr(totQty) : "0")}</td>")
            .Append($"<td style='{tdsB}'></td>")
            .Append($"<td style='{tdrB}'></td>");
        if (showDisc) { foot.Append($"<td style='{tdrB}'></td>"); foot.Append($"<td style='{tdrB}'>{Money(totDisc, nf)}</td>"); }
        foot.Append($"<td style='{tdrB}'>{Money(totExcl, nf)}</td>");
        if (showTax) { foot.Append($"<td style='{tdrB}'></td>"); foot.Append($"<td style='{tdrB}'>{Money(totTax, nf)}</td>"); }
        if (showChg)
        {
            foot.Append($"<td style='{tdrB}'>{Money(totComm, nf)}</td>");
            foot.Append($"<td style='{tdrB}'>{Money(totFr, nf)}</td>");
            foot.Append($"<td style='{tdrB}'>{Money(totLd, nf)}</td>");
        }
        foot.Append($"<td style='{tdrB}'>{Money(totTax, nf)}</td>");
        foot.Append($"<td style='{tdrB}'>{Money(totIncl, nf)}</td>");
        foot.Append($"<td style='{tdrB}'>{Money(totLine, nf)}</td>");

        string hd = $"padding:{pad};border:1px solid #1e293b;white-space:normal;text-align:center;vertical-align:middle;";
        var head = new StringBuilder();
        head.Append($"<th style='{hd}'>#</th><th style='{hd}'>SKU</th><th class='inv-desc' style='{hd}'>Description</th>")
            .Append($"<th style='{hd}'>Qty</th><th style='{hd}'>Unit</th><th style='{hd}'>Per Unit Price</th>");
        if (showDisc) head.Append($"<th style='{hd}'>Discount %</th><th style='{hd}'>Discount allowed</th>");
        head.Append($"<th style='{hd}'>Amount Excl. of Sales Tax</th>");
        if (showTax) head.Append($"<th style='{hd}'>Sales Tax %</th><th style='{hd}'>Sales Tax Amount per Unit</th>");
        if (showChg) head.Append($"<th style='{hd}'>Commission</th><th style='{hd}'>Freight</th><th style='{hd}'>Ld/Unld</th>");
        head.Append($"<th style='{hd}'>Total Sales Tax</th><th style='{hd}'>Amount Incl. of Sales Tax</th><th style='{hd}'>Total</th>");

        string itemsTable = $"<table class=\"inv-items\" style=\"width:100%;border-collapse:collapse;font-size:{Py.Repr(fs)}px;\">" +
                            "<thead><tr style=\"background:#1e293b;color:#fff;\">" + head + "</tr></thead><tbody>" + rows +
                            "<tr>" + foot + "</tr></tbody></table>";

        var display = new Dictionary<string, object>(topts);
        if (invoice.DiscountMode == "individual" && InvoiceTemplates.Str(topts, "discount_display") == "per_line") display["show_discount"] = false;
        if (invoice.TaxMode == "individual" && InvoiceTemplates.Str(topts, "tax_display") == "per_line") display["show_tax"] = false;
        if (invoice.ExpensesMode == "individual" && InvoiceTemplates.Str(topts, "charges_display") == "per_line")
        {
            display["show_commission"] = false;
            display["show_freight"] = false;
            display["show_loading"] = false;
            display["show_withholding"] = false;
        }
        double net = Py.F(invoice.NetPayable);
        if (net == 0)
            net = Py.F(invoice.Subtotal) - Py.F(invoice.TotalDiscount) + Py.F(invoice.TotalTax) - Py.F(invoice.TotalWithholdingTax);
        var ctx = CompanyCtx(company);
        ctx.AddRange(
        [
            new("invoice_no", E(invoice.VoucherNumber)),
            new("invoice_date", Date(invoice.InvoiceDate)),
            new("due_date", ""),
            new("status", invoice.ApprovedAt is not null ? "Approved" : "Unapproved"),
        ]);
        ctx.AddRange(PartyCtx(supplier?.Name, supplier?.Address, supplier?.City, supplier?.Phone, supplier?.Email, supplier?.TaxId));
        ctx.AddRange(
        [
            new("items_table", itemsTable),
            new("totals_table", InvoiceTemplates.BuildTotalsTable("purchase", display, string.IsNullOrEmpty(tpl.AccentColor) ? "#0f766e" : tpl.AccentColor)),
            new("subtotal", Money(Py.F(invoice.Subtotal), nf)),
            new("discount", Money(Py.F(invoice.TotalDiscount), nf)),
            new("tax", Money(Py.F(invoice.TotalTax), nf)),
            new("commission", Money(0, nf)),
            new("freight", Money(0, nf)),
            new("loading_unloading", Money(Py.F(invoice.TotalExpenses), nf)),
            new("withholding_tax", Money(Py.F(invoice.TotalWithholdingTax), nf)),
            new("grand_total", Money(net, nf)),
            new("delivery_charges", Money(0, nf)),
            new("installation_charges", Money(0, nf)),
            new("notes", E(invoice.Notes)),
        ]);
        return InvoiceTemplates.Render(tpl.BodyHtml, ctx);
    }
}
