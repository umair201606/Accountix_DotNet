using System.Text;

namespace Accountix.Invoicing.Services;

/// <summary>
/// The invoice-template designer's builders (shared/models/invoice_template.py):
/// option groups, build_body for the five designs, the preview's sample items
/// table and sample context, strip_shared_sheet_assets. Output is byte-identical
/// to Python's (parity-tested in Accountix.Platform.Tests). Added by the
/// Platform engineer for Settings → Invoice Templates; the constants are
/// generated (InvoiceDesigns.g.cs).
/// </summary>
public static partial class InvoiceDesigns
{
    public static readonly IReadOnlySet<string> StringOptions = new HashSet<string> { "discount_display", "tax_display", "charges_display" };

    public static IReadOnlyList<string> DesignKeys => Designs.Select(d => d.Key).ToList();

    /// <summary>option_groups(doc_type).</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<(string Key, string Label, string? Help)> Fields)> OptionGroups(string docType) =>
        docType == "sales" ? Groups_sales : Groups_purchase;

    private static string PageOpen => SheetCss + SheetFitJs + $"<div class=\"inv-sheet {SheetMarker}\">";
    private const string PageClose = "<div class=\"inv-pagenums\" aria-hidden=\"true\"></div></div>";

    private static bool On(IReadOnlyDictionary<string, object> o, string k) => InvoiceTemplates.On(o, k);
    private static string? S(IReadOnlyDictionary<string, object> o, string k) => InvoiceTemplates.Str(o, k);

    private static string PartyBlock(string docType, IReadOnlyDictionary<string, object> opts, string labelStyle = "")
    {
        string heading = docType == "sales" ? "Bill To" : "From";
        var parts = new List<string>
        {
            $"<div style=\"{labelStyle}\">{heading}</div>",
            "<div style=\"font-weight:700;font-size:15px;margin:4px 0 2px;\">{{party_name}}</div>",
        };
        if (On(opts, "show_party_address"))
        {
            parts.Add("<div style=\"color:#475569;\">{{party_address}}</div>");
            parts.Add("<div style=\"color:#475569;\">{{party_city}}</div>");
        }
        if (On(opts, "show_party_contact"))
        {
            parts.Add("<div style=\"color:#475569;\">{{party_phone}}</div>");
            parts.Add("<div style=\"color:#475569;\">{{party_email}}</div>");
        }
        if (On(opts, "show_party_tax_id"))
            parts.Add("<div style=\"color:#475569;margin-top:3px;\">NTN {{party_tax_id}}</div>");
        return string.Join("\n        ", parts);
    }

    private static string MetaRows(IReadOnlyDictionary<string, object> opts, string muted = "#64748b")
    {
        var rows = new List<string>
        {
            $"<div><span style=\"color:{muted};\">Invoice #</span> <strong>{{{{invoice_no}}}}</strong></div>",
            $"<div><span style=\"color:{muted};\">Date</span> <strong>{{{{invoice_date}}}}</strong></div>",
        };
        if (On(opts, "show_due_date"))
            rows.Add($"<div><span style=\"color:{muted};\">Due</span> <strong>{{{{due_date}}}}</strong></div>");
        if (On(opts, "show_status"))
            rows.Add($"<div><span style=\"color:{muted};\">Status</span> <strong>{{{{status}}}}</strong></div>");
        return string.Join("\n        ", rows);
    }

    private static string Footer(string docType, IReadOnlyDictionary<string, object> opts, string accent)
    {
        var output = new List<string>();
        if (On(opts, "show_notes"))
            output.Add("<div style=\"margin-top:26px;padding-top:12px;border-top:1px solid #e2e8f0;font-size:12px;color:#475569;\">" +
                       "<div style=\"font-weight:700;color:#0f172a;margin-bottom:3px;\">Notes</div>{{notes}}</div>");
        var bottom = new List<string>();
        if (On(opts, "show_signature"))
        {
            string left = docType == "sales" ? "Prepared By" : "Checked By";
            string right = docType == "sales" ? "Authorised Signatory" : "Approved By";
            bottom.Add("<table style=\"width:100%;margin-top:44px;border-collapse:collapse;\"><tr>" +
                       $"<td style=\"width:45%;border-top:1px solid #94a3b8;padding-top:6px;font-size:11px;color:#64748b;text-align:center;\">{left}</td>" +
                       "<td style=\"width:10%;\"></td>" +
                       $"<td style=\"width:45%;border-top:1px solid #94a3b8;padding-top:6px;font-size:11px;color:#64748b;text-align:center;\">{right}</td>" +
                       "</tr></table>");
        }
        if (On(opts, "show_thanks"))
        {
            string msg = docType == "sales" ? "Thank you for your business." : "This document is for internal record.";
            bottom.Add($"<div style=\"margin-top:22px;text-align:center;font-size:11px;color:{accent};letter-spacing:.4px;\">{msg}</div>");
        }
        if (bottom.Count > 0)
            output.Add("<div class=\"inv-foot\">" + string.Join("\n    ", bottom) + "</div>");
        return string.Join("\n  ", output);
    }

    private static string Title(string docType) => docType == "sales" ? "SALES INVOICE" : "PURCHASE INVOICE";
    private static string Logo(IReadOnlyDictionary<string, object> opts) => On(opts, "show_logo") ? "{{company_logo}}" : "";

    private static string CompanyTax(IReadOnlyDictionary<string, object> opts, string style = "color:#64748b;") =>
        On(opts, "show_company_tax_id") ? $"<div style=\"{style}\">NTN {{{{company_tax_id}}}}</div>" : "";

    private const string Items = "{{items_table}}\n  {{totals_table}}";

    private static string Classic(string d, IReadOnlyDictionary<string, object> o, string a) => $$"""
        {{PageOpen}}
          <div style="text-align:center;padding-bottom:16px;">
            {{Logo(o)}}
            <div style="font-size:24px;font-weight:800;letter-spacing:.5px;margin-top:6px;">{{"{{company_name}}"}}</div>
            <div style="color:#475569;margin-top:3px;">{{"{{company_address}}"}}, {{"{{company_city}}"}}</div>
            <div style="color:#475569;">{{"{{company_phone}}"}} &nbsp;&bull;&nbsp; {{"{{company_email}}"}}</div>
            {{CompanyTax(o)}}
          </div>
          <div style="border-top:3px double {{a}};margin:4px 0 18px;"></div>
          <div style="text-align:center;font-size:15px;font-weight:800;letter-spacing:3px;color:{{a}};margin-bottom:18px;">{{Title(d)}}</div>
          <table style="width:100%;border-collapse:collapse;margin-bottom:18px;">
            <tr>
              <td style="width:55%;vertical-align:top;">
                {{PartyBlock(d, o, "font-size:10px;font-weight:700;letter-spacing:1px;color:#64748b;text-transform:uppercase;")}}
              </td>
              <td style="width:45%;vertical-align:top;text-align:right;line-height:1.9;">
                {{MetaRows(o)}}
              </td>
            </tr>
          </table>
          {{Items}}
          {{Footer(d, o, a)}}
        {{PageClose}}
        """;

    private static string Modern(string d, IReadOnlyDictionary<string, object> o, string a) => $$"""
        {{PageOpen}}
          <div style="height:6px;background:{{a}};border-radius:3px;margin-bottom:22px;"></div>
          <table style="width:100%;border-collapse:collapse;margin-bottom:24px;">
            <tr>
              <td style="width:55%;vertical-align:top;">
                {{Logo(o)}}
                <div style="font-size:20px;font-weight:800;margin-top:4px;">{{"{{company_name}}"}}</div>
                <div style="color:#475569;margin-top:3px;">{{"{{company_address}}"}}, {{"{{company_city}}"}}</div>
                <div style="color:#475569;">{{"{{company_phone}}"}} &nbsp;&bull;&nbsp; {{"{{company_email}}"}}</div>
                {{CompanyTax(o)}}
              </td>
              <td style="width:45%;vertical-align:top;text-align:right;">
                <div style="font-size:26px;font-weight:800;color:{{a}};letter-spacing:1px;">{{Title(d)}}</div>
                <div style="margin-top:10px;line-height:1.9;">
                {{MetaRows(o)}}
                </div>
              </td>
            </tr>
          </table>
          <div style="background:#f8fafc;border-left:3px solid {{a}};padding:12px 14px;border-radius:0 6px 6px 0;margin-bottom:20px;">
            {{PartyBlock(d, o, "font-size:10px;font-weight:700;letter-spacing:1px;color:" + a + ";text-transform:uppercase;")}}
          </div>
          {{Items}}
          {{Footer(d, o, a)}}
        {{PageClose}}
        """;

    private static string Minimal(string d, IReadOnlyDictionary<string, object> o, string a) => $$"""
        {{PageOpen}}
          <table style="width:100%;border-collapse:collapse;margin-bottom:40px;">
            <tr>
              <td style="width:60%;vertical-align:top;">
                {{Logo(o)}}
                <div style="font-size:16px;font-weight:700;letter-spacing:.3px;margin-top:4px;">{{"{{company_name}}"}}</div>
                <div style="color:#94a3b8;font-size:12px;margin-top:2px;">{{"{{company_address}}"}}, {{"{{company_city}}"}}</div>
                <div style="color:#94a3b8;font-size:12px;">{{"{{company_phone}}"}} &nbsp;&bull;&nbsp; {{"{{company_email}}"}}</div>
                {{CompanyTax(o, "color:#94a3b8;font-size:12px;")}}
              </td>
              <td style="width:40%;vertical-align:top;text-align:right;">
                <div style="font-size:11px;font-weight:700;letter-spacing:3px;color:#94a3b8;">{{Title(d)}}</div>
                <div style="margin-top:8px;line-height:1.9;font-size:12px;">
                {{MetaRows(o, "#94a3b8")}}
                </div>
              </td>
            </tr>
          </table>
          <div style="margin-bottom:24px;">
            {{PartyBlock(d, o, "font-size:10px;font-weight:700;letter-spacing:1.5px;color:#94a3b8;text-transform:uppercase;")}}
          </div>
          {{Items}}
          {{Footer(d, o, a)}}
        {{PageClose}}
        """;

    private static string Bold(string d, IReadOnlyDictionary<string, object> o, string a) => $$"""
        {{PageOpen}}
          <table style="width:100%;border-collapse:collapse;background:{{a}};border-radius:8px;margin-bottom:24px;">
            <tr>
              <td style="padding:22px 24px;vertical-align:middle;">
                {{Logo(o)}}
                <div style="font-size:22px;font-weight:800;color:#fff;margin-top:4px;">{{"{{company_name}}"}}</div>
                <div style="color:rgba(255,255,255,.85);font-size:12px;margin-top:3px;">{{"{{company_address}}"}}, {{"{{company_city}}"}}</div>
                <div style="color:rgba(255,255,255,.85);font-size:12px;">{{"{{company_phone}}"}} &nbsp;&bull;&nbsp; {{"{{company_email}}"}}</div>
              </td>
              <td style="padding:22px 24px;vertical-align:middle;text-align:right;">
                <div style="font-size:22px;font-weight:800;color:#fff;letter-spacing:1px;">{{Title(d)}}</div>
                <div style="margin-top:8px;line-height:1.9;color:#fff;font-size:12px;">
                {{MetaRows(o, "rgba(255,255,255,.72)")}}
                </div>
              </td>
            </tr>
          </table>
          <table style="width:100%;border-collapse:collapse;margin-bottom:20px;">
            <tr>
              <td style="width:60%;vertical-align:top;">
                {{PartyBlock(d, o, "font-size:10px;font-weight:700;letter-spacing:1px;color:" + a + ";text-transform:uppercase;")}}
              </td>
              <td style="width:40%;vertical-align:top;text-align:right;">
                {{CompanyTax(o, "color:#64748b;font-size:12px;")}}
              </td>
            </tr>
          </table>
          {{Items}}
          {{Footer(d, o, a)}}
        {{PageClose}}
        """;

    private static string Letterhead(string d, IReadOnlyDictionary<string, object> o, string a) => $$"""
        {{PageOpen}}
          <table style="width:100%;border-collapse:collapse;">
            <tr>
              <td style="width:34mm;vertical-align:middle;">{{Logo(o)}}</td>
              <td style="vertical-align:middle;text-align:center;padding:0 4mm;">
                <div style="font-size:22px;font-weight:800;letter-spacing:.3px;line-height:1.2;">{{"{{company_name}}"}}</div>
                <div style="color:#475569;font-size:11.5px;margin-top:3px;">{{"{{company_address}}"}}, {{"{{company_city}}"}}</div>
                <div style="color:#475569;font-size:11.5px;">{{"{{company_phone}}"}} &nbsp;&bull;&nbsp; {{"{{company_email}}"}}</div>
                {{CompanyTax(o, "color:#64748b;font-size:11.5px;")}}
              </td>
              <!-- Balances the logo cell so the company name is centred on the page,
                   not on the space left over beside the logo. The invoice number and
                   date live in the Bill To row below, once. -->
              <td style="width:34mm;"></td>
            </tr>
          </table>
          <div style="border-top:2px solid {{a}};margin:10px 0 0;"></div>
          <div style="text-align:center;font-size:13px;font-weight:800;letter-spacing:2.5px;
            color:{{a}};margin:12px 0 16px;">{{Title(d)}}</div>
          <table style="width:100%;border-collapse:collapse;margin-bottom:16px;">
            <tr>
              <td style="width:55%;vertical-align:top;">
                {{PartyBlock(d, o, "font-size:10px;font-weight:700;letter-spacing:1px;color:#64748b;text-transform:uppercase;")}}
              </td>
              <td style="width:45%;vertical-align:top;text-align:right;line-height:1.9;">
                {{MetaRows(o)}}
              </td>
            </tr>
          </table>
          {{Items}}
          {{Footer(d, o, a)}}
        {{PageClose}}
        """;

    /// <summary>build_body(design, doc_type, accent, options): a design + toggles compiled to template HTML.</summary>
    public static string BuildBody(string? design, string? docType, string? accentColor, IReadOnlyDictionary<string, object> options)
    {
        string d = docType == "sales" ? "sales" : "purchase";
        string accent = (accentColor ?? "").StartsWith('#') ? accentColor! : "#0f766e";
        var o = Normalise(d, options);
        return design switch
        {
            "modern" => Modern(d, o, accent),
            "minimal" => Minimal(d, o, accent),
            "bold" => Bold(d, o, accent),
            "letterhead" => Letterhead(d, o, accent),
            _ => Classic(d, o, accent),
        };
    }

    /// <summary>normalise_options on a dict: unknown keys dropped, missing ones defaulted.</summary>
    public static Dictionary<string, object> Normalise(string docType, IReadOnlyDictionary<string, object>? raw)
    {
        var known = InvoiceTemplates.DefaultOptions(docType);
        if (raw is null || raw.Count == 0) return known;
        var output = new Dictionary<string, object>();
        foreach (var (k, v) in known)
        {
            raw.TryGetValue(k, out var got);
            if (StringOptions.Contains(k))
                output[k] = got is string s && s is "per_line" or "combined" ? s : v;
            else
                output[k] = got is null ? (bool)v : got is true || (got is string str && str.Length > 0);
        }
        return output;
    }

    /// <summary>The keys in option_groups order, for json.dumps(opts) as Python writes options_json.</summary>
    public static IEnumerable<string> OptionKeys(string docType) => OptionGroups(docType).SelectMany(g => g.Fields).Select(f => f.Key);

    /// <summary>strip_shared_sheet_assets: the shared stylesheet and script removed (bulk prints keep them once).</summary>
    public static string StripSharedSheetAssets(string? body) =>
        string.IsNullOrEmpty(body) ? body ?? "" : body.Replace(SheetCss, "").Replace(SheetFitJs, "");

    /// <summary>_sample_items_table: the preview's items table, the same shape the print routes build.</summary>
    public static string SampleItemsTable(string docType, IReadOnlyDictionary<string, object>? opts, string? numberFormat)
    {
        opts ??= new Dictionary<string, object>();
        bool disc = On(opts, "show_discount") && S(opts, "discount_display") == "per_line";
        bool tax = On(opts, "show_tax") && S(opts, "tax_display") == "per_line";
        bool chg = S(opts, "charges_display") == "per_line";
        (int N, string Sku, string Desc, int Qty, string Unit, double Price, double Dp, double Da, double Tp, double C1, double C2, double C3)[] items =
            docType == "sales"
                ?
                [
                    (1, "SKU-1001", "Solar Panel 550W Monocrystalline", 12, "pcs", 24500.00, 5.0, 14700.00, 16.0, 4000.00, 8333.00, 0),
                    (2, "SKU-1002", "Hybrid Inverter 8kW", 2, "pcs", 185000.00, 5.0, 18500.00, 16.0, 6000.00, 5000.00, 0),
                    (3, "SKU-1180", "Mounting Rail 4.2m Aluminium", 30, "pcs", 3200.00, 5.0, 4800.00, 16.0, 2000.00, 3334.00, 0),
                ]
                :
                [
                    (1, "SKU-1001", "Solar Panel 550W Monocrystalline", 12, "pcs", 24500.00, 5.0, 14700.00, 16.0, 2833.00, 4667.00, 1400.00),
                    (2, "SKU-1002", "Hybrid Inverter 8kW", 2, "pcs", 185000.00, 5.0, 18500.00, 16.0, 3500.00, 5333.00, 1800.00),
                    (3, "SKU-1180", "Mounting Rail 4.2m Aluminium", 30, "pcs", 3200.00, 5.0, 4800.00, 16.0, 2167.00, 4000.00, 1000.00),
                ];
        int nCols = 10 + (disc ? 2 : 0) + (tax ? 2 : 0) + (chg ? (docType == "sales" ? 2 : 3) : 0);
        var (font, pad) = InvoiceTemplates.ItemsTableMetrics(nCols);
        string M(double v) => Py.Amount(v, 2, numberFormat ?? "en");
        string tds = $"padding:{pad};border:1px solid #e2e8f0;white-space:nowrap;";
        string tdc = tds + "text-align:center;";
        string tdr = tds + "text-align:right;";
        int totQty = 0;
        double totDisc = 0, totExcl = 0, totC1 = 0, totC2 = 0, totC3 = 0, totTax = 0, totIncl = 0, totLine = 0;
        var body = new StringBuilder();
        foreach (var it in items)
        {
            double lineTotal = it.Price * it.Qty;
            double excl = lineTotal - it.Da;
            double taxAmt = excl * it.Tp / 100;
            double incl = excl + taxAmt;
            totQty += it.Qty; totDisc += it.Da; totExcl += excl; totC1 += it.C1; totC2 += it.C2; totC3 += it.C3;
            totTax += taxAmt; totIncl += incl; totLine += lineTotal;
            var c = new StringBuilder();
            c.Append($"<td style='{tdc}'>{it.N}</td><td style='{tds}'>{it.Sku}</td>")
             .Append($"<td class='inv-desc' style='{tds}white-space:normal;'>{it.Desc}</td>")
             .Append($"<td style='{tdc}'>{it.Qty}</td><td style='{tdc}'>{it.Unit}</td><td style='{tdr}'>{M(it.Price)}</td>");
            if (disc) c.Append($"<td style='{tdr}'>{Py.Fixed(it.Dp, 1)}%</td><td style='{tdr}'>{M(it.Da)}</td>");
            c.Append($"<td style='{tdr}'>{M(excl)}</td>");
            if (tax) c.Append($"<td style='{tdr}'>{Py.Fixed(it.Tp, 1)}%</td><td style='{tdr}'>{M(taxAmt)}</td>");
            if (chg)
            {
                c.Append($"<td style='{tdr}'>{M(it.C1)}</td><td style='{tdr}'>{M(it.C2)}</td>");
                if (docType != "sales") c.Append($"<td style='{tdr}'>{M(it.C3)}</td>");
            }
            c.Append($"<td style='{tdr}'>{M(taxAmt)}</td><td style='{tdr}'>{M(incl)}</td><td style='{tdr}'>{M(lineTotal)}</td>");
            body.Append("<tr>").Append(c).Append("</tr>");
        }
        string tdsB = $"padding:{pad};border:1px solid #e2e8f0;font-weight:700;background:#f1f5f9;white-space:nowrap;";
        string tdrB = tdsB + "text-align:right;";
        var foot = new StringBuilder();
        foot.Append($"<td style='{tdsB};text-align:center;'></td><td style='{tdsB}'></td>")
            .Append($"<td class='inv-desc' style='{tdsB}'>Total</td><td style='{tdsB};text-align:center;'>{totQty}</td>")
            .Append($"<td style='{tdsB}'></td><td style='{tdrB}'></td>");
        if (disc) foot.Append($"<td style='{tdrB}'></td><td style='{tdrB}'>{M(totDisc)}</td>");
        foot.Append($"<td style='{tdrB}'>{M(totExcl)}</td>");
        if (tax) foot.Append($"<td style='{tdrB}'></td><td style='{tdrB}'>{M(totTax)}</td>");
        if (chg)
        {
            foot.Append($"<td style='{tdrB}'>{M(totC1)}</td><td style='{tdrB}'>{M(totC2)}</td>");
            if (docType != "sales") foot.Append($"<td style='{tdrB}'>{M(totC3)}</td>");
        }
        foot.Append($"<td style='{tdrB}'>{M(totTax)}</td><td style='{tdrB}'>{M(totIncl)}</td><td style='{tdrB}'>{M(totLine)}</td>");
        string hd = $"padding:{pad};border:1px solid #1e293b;white-space:normal;text-align:center;vertical-align:middle;";
        var head = new StringBuilder();
        head.Append($"<th style='{hd}'>#</th><th style='{hd}'>SKU</th><th class='inv-desc' style='{hd}'>Description</th>")
            .Append($"<th style='{hd}'>Qty</th><th style='{hd}'>Unit</th><th style='{hd}'>Per Unit Price</th>");
        if (disc) head.Append($"<th style='{hd}'>Discount %</th><th style='{hd}'>Discount allowed</th>");
        head.Append($"<th style='{hd}'>Amount Excl. of Sales Tax</th>");
        if (tax) head.Append($"<th style='{hd}'>Sales Tax %</th><th style='{hd}'>Sales Tax Amount per Unit</th>");
        if (chg)
            head.Append(docType == "sales"
                ? $"<th style='{hd}'>Carriage Expense</th><th style='{hd}'>Installation</th>"
                : $"<th style='{hd}'>Commission</th><th style='{hd}'>Freight</th><th style='{hd}'>Ld/Unld</th>");
        head.Append($"<th style='{hd}'>Total Sales Tax</th><th style='{hd}'>Amount Incl. of Sales Tax</th><th style='{hd}'>Total</th>");
        return $"<table class=\"inv-items\" style=\"width:100%;border-collapse:collapse;font-size:{Py.Repr(font)}px;\">" +
               "<thead><tr style=\"background:#1e293b;color:#fff;\">" + head + "</tr></thead><tbody>" + body +
               "<tr>" + foot + "</tr></tbody></table>";
    }

    /// <summary>sample_context: stand-in data for the preview, the company's own profile where filled in.</summary>
    public static List<KeyValuePair<string, string?>> SampleContext(string docType, CompanyInfo? company, IReadOnlyDictionary<string, object>? opts,
                                                                    DateTime utcNow, string? numberFormat)
    {
        string C(string? v, string fallback) => string.IsNullOrEmpty(v) ? fallback : v;
        string logo = company?.LogoUrl is { Length: > 0 } url
            ? $"<img src=\"{url.Replace("\"", "&quot;")}\" style=\"max-height:60px;\" alt=\"Logo\">" : "";
        bool sales = docType == "sales";
        var (pn, pa, pc, pp, pe, pt) = sales
            ? ("Meezan Traders (Pvt) Ltd", "14-B Gulberg III", "Lahore", "+92 42 3577 1200", "accounts@meezantraders.pk", "3520112-8")
            : ("Zenith Solar Supplies", "Plot 22, SITE Area", "Karachi", "+92 21 3255 8800", "sales@zenithsolar.pk", "1790443-2");
        string M(double v) => Py.Amount(v, 2, numberFormat ?? "en");
        string today = utcNow.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
        string name = C(company?.CompanyName, "Your Company Name");
        return
        [
            new("company_logo", logo),
            new("company_name", name),
            new("company_address", C(company?.Address, "123 Business Road")),
            new("company_city", C(company?.City, "Lahore")),
            new("company_phone", C(company?.Phone, "+92 42 1234 5678")),
            new("company_email", C(company?.Email, "info@yourcompany.pk")),
            new("company_tax_id", C(company?.TaxId, "1234567-8")),
            new("company_bank_name", C(company?.BankName, "Meezan Bank Ltd, Gulberg Branch")),
            new("company_bank_account_title", C(company?.BankAccountTitle, name)),
            new("company_bank_account_number", C(company?.BankAccountNumber, "0102-0101234567-01")),
            new("invoice_no", sales ? "VCH-202607-0042" : "PINV-202607-0042"),
            new("invoice_date", today),
            new("due_date", today),
            new("status", "Approved"),
            new("party_name", pn), new("party_address", pa), new("party_city", pc),
            new("party_phone", pp), new("party_email", pe), new("party_tax_id", pt),
            new("items_table", SampleItemsTable(docType, opts, numberFormat)),
            new("totals_table", InvoiceTemplates.BuildTotalsTable(docType, opts ?? InvoiceTemplates.DefaultOptions(docType), "#0f766e")),
            new("subtotal", M(760000)), new("discount", M(38000)), new("tax", M(122760)),
            new("delivery_charges", M(12000)), new("installation_charges", M(25000)),
            new("commission", M(8500)), new("freight", M(14000)), new("loading_unloading", M(4200)),
            new("withholding_tax", M(7600)), new("grand_total", M(881760)),
            new("notes", "Payment due within 30 days. Goods remain the property of the seller until paid in full."),
        ];
    }
}
