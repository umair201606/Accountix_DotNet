// Generated once from the former Python app (29 Oct 2026): the invoice designs, A4 stylesheet and sample data of the template designer.
// The generator went with the Python app, so this file is now maintained by hand.
#nullable enable
namespace Accountix.Invoicing.Services;

public static partial class InvoiceDesigns
{
    /// <summary>_SHEET_CSS: the shared A4 stylesheet every design opens with.</summary>
    public const string SheetCss = "<style>\n@page{size:A4;margin:14mm}\n.inv-sheet{width:210mm;min-height:297mm;box-sizing:border-box;padding:14mm;\n  margin:0 auto;background:#fff;color:#0f172a;font-size:13px;line-height:1.5;\n  font-family:Inter,Segoe UI,Arial,sans-serif;\n  position:relative;display:flex;flex-direction:column}\n.inv-sheet *{box-sizing:border-box}\n/* Flex items do not shrink-to-fit the way blocks do; without this a table or a\n   heading can end up narrower than the page. */\n.inv-sheet>*{flex:none;width:100%}\n\n/* Signatures and the closing line belong at the foot of the sheet, not\n   wherever the item list happened to stop. The bottom padding is the strip the\n   page number sits in, so the closing line does not share its baseline. */\n.inv-sheet>.inv-foot{margin-top:auto;padding-top:10mm;padding-bottom:7mm}\n\n/* Page numbers, bottom-right. Stamped by script because the count depends on\n   how far the content actually runs, and Chromium does not implement the\n   @page margin boxes that would otherwise carry counter(page). */\n.inv-pagenums{position:absolute;left:14mm;right:14mm;top:14mm;bottom:14mm;\n  width:auto;pointer-events:none}\n.inv-pageno{position:absolute;right:0;font-size:9px;color:#94a3b8;\n  letter-spacing:.3px;white-space:nowrap}\n\n/* Items table: columns size themselves to their content, and only the\n   description is allowed to wrap. Without the nowrap a long header squeezes a\n   money column until \"1,234,567.00\" breaks across two lines.\n   Size is set once, on the table — cells inherit it and pad in em — so the\n   auto-fit below can resize the whole table by changing a single property. */\n.inv-sheet table.inv-items{width:100%;border-collapse:collapse;table-layout:auto}\n.inv-sheet table.inv-items th,\n.inv-sheet table.inv-items td{white-space:nowrap;overflow-wrap:normal}\n/* Headers read as labels for the column, so they sit centred in the cell both\n   ways — including the tall ones that wrap to two or three lines. The body\n   cells keep their own alignment: numbers right, codes left. */\n.inv-sheet table.inv-items th{white-space:normal;word-break:normal;\n  overflow-wrap:break-word;text-align:center;vertical-align:middle;\n  line-height:1.25;font-size:.95em}\n/* Every other column takes exactly the width its content needs — width:1% on a\n   nowrap cell resolves to min-content — so the slack all lands on the\n   description instead of being shared out and leaving it to wrap early. */\n.inv-sheet table.inv-items th:not(.inv-desc),\n.inv-sheet table.inv-items td:not(.inv-desc){width:1%}\n/* The description absorbs the width the other columns do not need, and is the\n   one cell that may run to a second line. */\n.inv-sheet table.inv-items td.inv-desc{white-space:normal;overflow-wrap:break-word;\n  width:100%;min-width:22mm}\n.inv-sheet table.inv-items th.inv-desc{width:100%;min-width:22mm}\n.inv-sheet table.inv-items tr{page-break-inside:avoid}\n.inv-sheet table.inv-items thead{display:table-header-group}\n\n/* The summary stays pinned to the right edge at a fixed width, so adding or\n   removing item columns moves it not at all. */\n.inv-sheet table.inv-totals{width:100%;border-collapse:collapse;margin-top:14px;\n  page-break-inside:avoid}\n.inv-sheet table.inv-totals>tbody>tr>td.inv-totals-gap{width:auto}\n.inv-sheet table.inv-totals>tbody>tr>td.inv-totals-box{width:72mm}\n\n@media print{\n  /* The @page margin is the paper margin; a second one on the sheet would\n     double it and push the table off the page again. The scale applied for\n     small screens has no business on paper, where the sheet is already the\n     size of the page. */\n  .inv-sheet{width:auto;min-height:0;padding:0;margin:0!important;\n    transform:none!important;zoom:1!important}\n  /* Padding is gone on paper — the @page margin is doing that job — so the\n     number strip has to sit flush with the content box instead. */\n  .inv-pagenums{left:0;right:0;top:0;bottom:0}\n  .inv-sheet table.inv-items th{background:#1e293b!important;color:#fff!important;\n    -webkit-print-color-adjust:exact;print-color-adjust:exact}\n}\n</style>";

    /// <summary>_SHEET_FIT_JS: the fit-to-page script.</summary>
    public const string SheetFitJs = "<script>\n(function () {\n  function fitOne(sheet, tbl) {\n    var cs = window.getComputedStyle(sheet);\n    var avail = sheet.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);\n    if (!avail || avail <= 0) { return; }\n    var start = parseFloat(tbl.getAttribute('data-basesize') || '0');\n    if (!start) {\n      start = parseFloat(window.getComputedStyle(tbl).fontSize) || 11;\n      tbl.setAttribute('data-basesize', start);\n    }\n    var size = start;\n    tbl.style.fontSize = size + 'px';\n    // Step down until it fits. The floor is the point past which the paper is\n    // not readable anyway; below it, let the description column wrap harder\n    // rather than shrink the type into illegibility.\n    for (var i = 0; i < 60 && tbl.scrollWidth > avail + 1 && size > 5.5; i++) {\n      size = Math.round((size - 0.25) * 100) / 100;\n      tbl.style.fontSize = size + 'px';\n    }\n    if (tbl.scrollWidth > avail + 1) {\n      tbl.style.tableLayout = 'fixed';\n      tbl.style.width = '100%';\n    }\n  }\n  // A sheet is a fixed 210mm because that is what A4 is. On any screen narrower\n  // than that — a phone, a split pane, the settings preview — show the whole\n  // page scaled down rather than letting it hang off the side or reflowing it\n  // into something that is no longer A4. What you see stays the page, just\n  // smaller, so the preview still answers \"does this fit on paper\".\n  //\n  // zoom rather than transform: zoom shrinks the layout box, so the document\n  // ends where the sheet ends. A transform only repaints, leaving the full\n  // 297mm reserved and a screenful of dead space under a scaled-down page.\n  var CAN_ZOOM = (typeof document.documentElement.style.zoom !== 'undefined');\n\n  function unscale(sheet) {\n    sheet.style.zoom = '';\n    sheet.style.transform = '';\n    sheet.style.marginRight = '';\n    sheet.style.marginBottom = '';\n  }\n\n  function scaleSheet(sheet) {\n    if (window.matchMedia && window.matchMedia('print').matches) { return; }\n    var box = sheet.parentElement || document.body;\n    var avail = box.clientWidth;\n    var natural = sheet.offsetWidth;\n    if (!avail || !natural || avail >= natural) { return; }\n    var k = avail / natural;\n    if (CAN_ZOOM) { sheet.style.zoom = k; return; }\n    // Fallback for engines without zoom: transform, then pull the leftover\n    // width and height back in so nothing scrolls to empty space.\n    sheet.style.transformOrigin = 'top left';\n    sheet.style.transform = 'scale(' + k + ')';\n    sheet.style.marginRight = -Math.round(natural - avail) + 'px';\n    sheet.style.marginBottom = -Math.round(sheet.offsetHeight * (1 - k)) + 'px';\n  }\n\n  // Millimetres in CSS pixels, measured rather than assumed — a page zoom or a\n  // non-96dpi setting changes it.\n  function mmPx() {\n    var probe = document.createElement('div');\n    probe.style.cssText = 'position:absolute;visibility:hidden;width:100mm;height:0';\n    document.body.appendChild(probe);\n    var px = probe.offsetWidth / 100;\n    document.body.removeChild(probe);\n    return px || (96 / 25.4);\n  }\n\n  // A4 minus the 14mm margins top and bottom: the strip of paper one page of\n  // content actually gets.\n  var PAGE_CONTENT_MM = 297 - 28;\n\n  function stampPages(sheet) {\n    var host = sheet.querySelector('.inv-pagenums');\n    if (!host) { return; }\n    host.innerHTML = '';\n    var mm = mmPx();\n    var per = PAGE_CONTENT_MM * mm;\n    if (!per || !host.clientHeight) { return; }\n    var printing = !!(window.matchMedia && window.matchMedia('print').matches);\n\n    // A hair of tolerance, or a page that fills exactly to its margin claims a\n    // second, empty one.\n    var pages = Math.max(1, Math.ceil((host.clientHeight - 2) / per));\n\n    // On screen the sheet is one continuous block, so round it up to whole\n    // pages. That keeps it looking like the stack of paper it will print as,\n    // and it puts the signature block — which floats to the bottom — at the\n    // foot of the last page rather than partway down it.\n    // On paper the browser is already doing the pagination; growing the sheet\n    // there would just add a blank page at the end.\n    sheet.style.minHeight = printing ? '' : (pages * per + 28 * mm) + 'px';\n\n    var total = host.clientHeight;\n    for (var i = 0; i < pages; i++) {\n      var tag = document.createElement('div');\n      tag.className = 'inv-pageno';\n      tag.textContent = 'Page ' + (i + 1) + ' of ' + pages;\n      host.appendChild(tag);\n      // Sit the tag's baseline just inside the bottom edge of each page's\n      // content strip — measured from its own height, so it is never half cut\n      // off by the edge — and never past where the content actually ends, or\n      // the last one is stranded outside the sheet and never drawn.\n      var h = tag.offsetHeight || 12;\n      var y = (i + 1) * per - h - 1;\n      tag.style.top = Math.max(0, Math.min(y, total - h - 1)) + 'px';\n    }\n  }\n\n  // A sheet is usually laid out inside something not visible yet — the preview\n  // pane behind a tab, the mobile preview before it slides up. Measured while\n  // hidden every width is zero, so the fit is skipped; nothing then fires when\n  // the container is finally shown and the page is left at full 210mm inside a\n  // phone-width frame, overflowing to the right with the summary off-screen.\n  // Watching the box it sits in catches that moment and every later resize.\n  //\n  // Wired from fitAll rather than on load: this script is emitted ahead of the\n  // sheet markup, so at the time it first runs there is no sheet to observe.\n  var ro = null;\n  function observeSheets(sheets) {\n    if (!window.ResizeObserver) { return; }\n    if (!ro) {\n      ro = new ResizeObserver(function (entries) {\n        // Width is what drives the scale. Refitting on a height change would\n        // chase the height the fit itself just produced.\n        var moved = false;\n        for (var i = 0; i < entries.length; i++) {\n          var el = entries[i].target;\n          var w = el.clientWidth;\n          if (el.__invLastW !== w) { el.__invLastW = w; moved = true; }\n        }\n        if (!moved) { return; }\n        clearTimeout(window.__invFitT);\n        window.__invFitT = setTimeout(fitAll, 60);\n      });\n    }\n    for (var s = 0; s < sheets.length; s++) {\n      var box = sheets[s].parentElement;\n      if (box && !box.__invObserved) {\n        box.__invObserved = true;\n        box.__invLastW = box.clientWidth;\n        ro.observe(box);\n      }\n    }\n  }\n\n  function fitAll() {\n    var sheets = document.querySelectorAll('.inv-sheet');\n    observeSheets(sheets);\n    for (var s = 0; s < sheets.length; s++) {\n      // Measure at true size: a scale left over from the last pass would make\n      // the sheet look narrower than A4 and shrink the type for no reason.\n      unscale(sheets[s]);\n      // Type first — the table has to fit the sheet's own 182mm whatever size\n      // the sheet is being displayed at.\n      var tables = sheets[s].querySelectorAll('table.inv-items');\n      for (var t = 0; t < tables.length; t++) { fitOne(sheets[s], tables[t]); }\n      // Then count pages, which depends on the height the fitted type produced.\n      stampPages(sheets[s]);\n      scaleSheet(sheets[s]);\n    }\n  }\n  if (document.readyState === 'loading') {\n    document.addEventListener('DOMContentLoaded', fitAll);\n  } else {\n    fitAll();\n  }\n  window.addEventListener('load', fitAll);\n  // ResizeObserver is the right tool for \"the pane just became visible\", but it\n  // is suspended while a tab is not rendering and absent on older engines. A\n  // few settling passes cover the common case — a preview revealed a moment\n  // after load — without depending on it. Bounded, so this cannot become a\n  // polling loop.\n  var settleAt = [200, 600, 1500];\n  for (var q = 0; q < settleAt.length; q++) { setTimeout(fitAll, settleAt[q]); }\n  // Print geometry differs from screen geometry, so measure again for paper.\n  if (window.matchMedia) {\n    try { window.matchMedia('print').addListener(fitAll); } catch (e) {}\n  }\n  window.addEventListener('beforeprint', fitAll);\n  window.addEventListener('resize', function () {\n    clearTimeout(window.__invFitT);\n    window.__invFitT = setTimeout(fitAll, 120);\n  });\n\n  window.invoiceFitSheets = fitAll;\n})();\n</script>";

    public const string SheetMarker = "inv-sheet-v1";

    /// <summary>DESIGNS: (key, label, description).</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Description)> Designs =
    [
        ("classic", "Classic", "Centred letterhead with ruled divisions. Formal and conservative — the look most customers expect from an invoice."),
        ("modern", "Modern", "Logo left, invoice details right, under a slim colour bar. Clean and current without being loud."),
        ("minimal", "Minimal", "Generous white space and hairline rules. Understated; lets the numbers speak."),
        ("bold", "Bold", "Solid colour header block. High contrast and easy to pick out of a pile of paperwork."),
        ("letterhead", "Letterhead", "Logo on the left with the company name centred across the page and the invoice number and date on the right. Customer on the left, invoice details on the right below it."),
    ];

    /// <summary>ACCENT_PRESETS: (colour, name).</summary>
    public static readonly IReadOnlyList<(string Color, string Name)> AccentPresets =
    [
        ("#0f766e", "Teal"),
        ("#1d4ed8", "Blue"),
        ("#6d28d9", "Purple"),
        ("#b91c1c", "Red"),
        ("#c2410c", "Orange"),
        ("#166534", "Green"),
        ("#334155", "Slate"),
        ("#0f172a", "Black"),
    ];

    /// <summary>PLACEHOLDER_HELP, in order.</summary>
    public static readonly IReadOnlyList<(string Key, string Help)> PlaceholderHelp =
    [
        ("company_name", "Company name"),
        ("company_address", "Company street address"),
        ("company_city", "Company city"),
        ("company_phone", "Company phone number"),
        ("company_email", "Company email"),
        ("company_tax_id", "Company tax/NTN number"),
        ("company_logo", "Company logo image tag"),
        ("company_bank_name", "Company bank name"),
        ("company_bank_account_title", "Company bank account title"),
        ("company_bank_account_number", "Company bank account number"),
        ("invoice_no", "Invoice / voucher number"),
        ("invoice_date", "Invoice date"),
        ("due_date", "Payment due date"),
        ("status", "Invoice status (approved/unapproved)"),
        ("party_name", "Customer or supplier name"),
        ("party_address", "Customer or supplier address"),
        ("party_city", "Customer or supplier city"),
        ("party_phone", "Customer or supplier phone"),
        ("party_email", "Customer or supplier email"),
        ("party_tax_id", "Customer or supplier tax ID"),
        ("items_table", "Full HTML table of invoice line items"),
        ("subtotal", "Subtotal amount"),
        ("discount", "Total discount amount"),
        ("tax", "Total sales tax amount"),
        ("grand_total", "Net total payable/receivable"),
        ("delivery_charges", "Delivery charges (sales only)"),
        ("installation_charges", "Installation charges (sales only)"),
        ("commission", "Commission (procurement only)"),
        ("freight", "Freight charges (procurement only)"),
        ("loading_unloading", "Loading/unloading charges (procurement only)"),
        ("withholding_tax", "Withholding tax (procurement only)"),
        ("notes", "Invoice notes"),
    ];

    private static readonly IReadOnlyList<(string Group, IReadOnlyList<(string Key, string Label, string? Help)> Fields)> Groups_sales =
    [
        ("Header", [("show_logo", "Company logo", "Your logo at the top of the page"), ("show_company_tax_id", "Your tax / NTN number", "Required on a tax invoice in most places"), ("show_status", "Approved / unapproved stamp", "Useful internally, usually hidden from customers"), ("show_due_date", "Due date", "When payment is expected")]),
        ("Customer details", [("show_party_address", "Their address", null), ("show_party_contact", "Their phone and email", null), ("show_party_tax_id", "Their tax / NTN number", "Needed if they claim input tax")]),
        ("Amounts", [("show_discount", "Discount line", "Hide to show only the discounted price"), ("show_tax", "Sales tax line", null), ("show_delivery", "Delivery charges", null), ("show_installation", "Installation charges", null), ("show_bank_details", "Bank details for payment", "Your bank name, account title and number, printed beside the totals")]),
        ("Columns", [("discount_display", "Discount columns", "Show discount % and amount per line item or as a combined total in the footer"), ("tax_display", "Tax columns", "Show sales tax % and amount per line item or as a combined total in the footer"), ("charges_display", "Charges columns", "Show charges (delivery/installation/commission etc.) per line item or as a combined total in the footer")]),
        ("Footer", [("show_notes", "Notes", "Whatever was typed in the invoice's notes box"), ("show_signature", "Signature lines", "Space to sign on the printed page"), ("show_thanks", "Closing line", null)]),
    ];

    private static readonly IReadOnlyList<(string Group, IReadOnlyList<(string Key, string Label, string? Help)> Fields)> Groups_purchase =
    [
        ("Header", [("show_logo", "Company logo", "Your logo at the top of the page"), ("show_company_tax_id", "Your tax / NTN number", "Required on a tax invoice in most places"), ("show_status", "Approved / unapproved stamp", "Useful internally, usually hidden from customers"), ("show_due_date", "Due date", "When payment is expected")]),
        ("Supplier details", [("show_party_address", "Their address", null), ("show_party_contact", "Their phone and email", null), ("show_party_tax_id", "Their tax / NTN number", "Needed if they claim input tax")]),
        ("Amounts", [("show_discount", "Discount line", "Hide to show only the discounted price"), ("show_tax", "Sales tax line", null), ("show_commission", "Commission", null), ("show_freight", "Freight", null), ("show_loading", "Loading / unloading", null), ("show_withholding", "Withholding tax", null)]),
        ("Columns", [("discount_display", "Discount columns", "Show discount % and amount per line item or as a combined total in the footer"), ("tax_display", "Tax columns", "Show sales tax % and amount per line item or as a combined total in the footer"), ("charges_display", "Charges columns", "Show charges (delivery/installation/commission etc.) per line item or as a combined total in the footer")]),
        ("Footer", [("show_notes", "Notes", "Whatever was typed in the invoice's notes box"), ("show_signature", "Signature lines", "Space to sign on the printed page"), ("show_thanks", "Closing line", null)]),
    ];

    /// <summary>DISPLAY_CHOICES.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Value, string Label)>> DisplayChoices = new Dictionary<string, IReadOnlyList<(string Value, string Label)>>
    {
        ["discount_display"] = [("per_line", "Per line item"), ("combined", "Combined total")],
        ["tax_display"] = [("per_line", "Per line item"), ("combined", "Combined total")],
        ["charges_display"] = [("per_line", "Per line item"), ("combined", "Combined total")],
    };
}
