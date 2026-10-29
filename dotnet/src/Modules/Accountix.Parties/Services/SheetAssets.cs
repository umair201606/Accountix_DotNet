namespace Accountix.Parties.Services;

/// <summary>
/// The shared A4 stylesheet and fit script every invoice print body carries
/// (shared/models/invoice_template.py _SHEET_CSS / _SHEET_FIT_JS), copied
/// byte for byte by a script so strip_shared_sheet_assets removes exactly
/// what the stored templates contain. Do not edit by hand.
/// </summary>
internal static class SheetAssets
{
    public const string Css = @"<style>
@page{size:A4;margin:14mm}
.inv-sheet{width:210mm;min-height:297mm;box-sizing:border-box;padding:14mm;
  margin:0 auto;background:#fff;color:#0f172a;font-size:13px;line-height:1.5;
  font-family:Inter,Segoe UI,Arial,sans-serif;
  position:relative;display:flex;flex-direction:column}
.inv-sheet *{box-sizing:border-box}
/* Flex items do not shrink-to-fit the way blocks do; without this a table or a
   heading can end up narrower than the page. */
.inv-sheet>*{flex:none;width:100%}

/* Signatures and the closing line belong at the foot of the sheet, not
   wherever the item list happened to stop. The bottom padding is the strip the
   page number sits in, so the closing line does not share its baseline. */
.inv-sheet>.inv-foot{margin-top:auto;padding-top:10mm;padding-bottom:7mm}

/* Page numbers, bottom-right. Stamped by script because the count depends on
   how far the content actually runs, and Chromium does not implement the
   @page margin boxes that would otherwise carry counter(page). */
.inv-pagenums{position:absolute;left:14mm;right:14mm;top:14mm;bottom:14mm;
  width:auto;pointer-events:none}
.inv-pageno{position:absolute;right:0;font-size:9px;color:#94a3b8;
  letter-spacing:.3px;white-space:nowrap}

/* Items table: columns size themselves to their content, and only the
   description is allowed to wrap. Without the nowrap a long header squeezes a
   money column until ""1,234,567.00"" breaks across two lines.
   Size is set once, on the table — cells inherit it and pad in em — so the
   auto-fit below can resize the whole table by changing a single property. */
.inv-sheet table.inv-items{width:100%;border-collapse:collapse;table-layout:auto}
.inv-sheet table.inv-items th,
.inv-sheet table.inv-items td{white-space:nowrap;overflow-wrap:normal}
/* Headers read as labels for the column, so they sit centred in the cell both
   ways — including the tall ones that wrap to two or three lines. The body
   cells keep their own alignment: numbers right, codes left. */
.inv-sheet table.inv-items th{white-space:normal;word-break:normal;
  overflow-wrap:break-word;text-align:center;vertical-align:middle;
  line-height:1.25;font-size:.95em}
/* Every other column takes exactly the width its content needs — width:1% on a
   nowrap cell resolves to min-content — so the slack all lands on the
   description instead of being shared out and leaving it to wrap early. */
.inv-sheet table.inv-items th:not(.inv-desc),
.inv-sheet table.inv-items td:not(.inv-desc){width:1%}
/* The description absorbs the width the other columns do not need, and is the
   one cell that may run to a second line. */
.inv-sheet table.inv-items td.inv-desc{white-space:normal;overflow-wrap:break-word;
  width:100%;min-width:22mm}
.inv-sheet table.inv-items th.inv-desc{width:100%;min-width:22mm}
.inv-sheet table.inv-items tr{page-break-inside:avoid}
.inv-sheet table.inv-items thead{display:table-header-group}

/* The summary stays pinned to the right edge at a fixed width, so adding or
   removing item columns moves it not at all. */
.inv-sheet table.inv-totals{width:100%;border-collapse:collapse;margin-top:14px;
  page-break-inside:avoid}
.inv-sheet table.inv-totals>tbody>tr>td.inv-totals-gap{width:auto}
.inv-sheet table.inv-totals>tbody>tr>td.inv-totals-box{width:72mm}

@media print{
  /* The @page margin is the paper margin; a second one on the sheet would
     double it and push the table off the page again. The scale applied for
     small screens has no business on paper, where the sheet is already the
     size of the page. */
  .inv-sheet{width:auto;min-height:0;padding:0;margin:0!important;
    transform:none!important;zoom:1!important}
  /* Padding is gone on paper — the @page margin is doing that job — so the
     number strip has to sit flush with the content box instead. */
  .inv-pagenums{left:0;right:0;top:0;bottom:0}
  .inv-sheet table.inv-items th{background:#1e293b!important;color:#fff!important;
    -webkit-print-color-adjust:exact;print-color-adjust:exact}
}
</style>";

    public const string FitJs = @"<script>
(function () {
  function fitOne(sheet, tbl) {
    var cs = window.getComputedStyle(sheet);
    var avail = sheet.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
    if (!avail || avail <= 0) { return; }
    var start = parseFloat(tbl.getAttribute('data-basesize') || '0');
    if (!start) {
      start = parseFloat(window.getComputedStyle(tbl).fontSize) || 11;
      tbl.setAttribute('data-basesize', start);
    }
    var size = start;
    tbl.style.fontSize = size + 'px';
    // Step down until it fits. The floor is the point past which the paper is
    // not readable anyway; below it, let the description column wrap harder
    // rather than shrink the type into illegibility.
    for (var i = 0; i < 60 && tbl.scrollWidth > avail + 1 && size > 5.5; i++) {
      size = Math.round((size - 0.25) * 100) / 100;
      tbl.style.fontSize = size + 'px';
    }
    if (tbl.scrollWidth > avail + 1) {
      tbl.style.tableLayout = 'fixed';
      tbl.style.width = '100%';
    }
  }
  // A sheet is a fixed 210mm because that is what A4 is. On any screen narrower
  // than that — a phone, a split pane, the settings preview — show the whole
  // page scaled down rather than letting it hang off the side or reflowing it
  // into something that is no longer A4. What you see stays the page, just
  // smaller, so the preview still answers ""does this fit on paper"".
  //
  // zoom rather than transform: zoom shrinks the layout box, so the document
  // ends where the sheet ends. A transform only repaints, leaving the full
  // 297mm reserved and a screenful of dead space under a scaled-down page.
  var CAN_ZOOM = (typeof document.documentElement.style.zoom !== 'undefined');

  function unscale(sheet) {
    sheet.style.zoom = '';
    sheet.style.transform = '';
    sheet.style.marginRight = '';
    sheet.style.marginBottom = '';
  }

  function scaleSheet(sheet) {
    if (window.matchMedia && window.matchMedia('print').matches) { return; }
    var box = sheet.parentElement || document.body;
    var avail = box.clientWidth;
    var natural = sheet.offsetWidth;
    if (!avail || !natural || avail >= natural) { return; }
    var k = avail / natural;
    if (CAN_ZOOM) { sheet.style.zoom = k; return; }
    // Fallback for engines without zoom: transform, then pull the leftover
    // width and height back in so nothing scrolls to empty space.
    sheet.style.transformOrigin = 'top left';
    sheet.style.transform = 'scale(' + k + ')';
    sheet.style.marginRight = -Math.round(natural - avail) + 'px';
    sheet.style.marginBottom = -Math.round(sheet.offsetHeight * (1 - k)) + 'px';
  }

  // Millimetres in CSS pixels, measured rather than assumed — a page zoom or a
  // non-96dpi setting changes it.
  function mmPx() {
    var probe = document.createElement('div');
    probe.style.cssText = 'position:absolute;visibility:hidden;width:100mm;height:0';
    document.body.appendChild(probe);
    var px = probe.offsetWidth / 100;
    document.body.removeChild(probe);
    return px || (96 / 25.4);
  }

  // A4 minus the 14mm margins top and bottom: the strip of paper one page of
  // content actually gets.
  var PAGE_CONTENT_MM = 297 - 28;

  function stampPages(sheet) {
    var host = sheet.querySelector('.inv-pagenums');
    if (!host) { return; }
    host.innerHTML = '';
    var mm = mmPx();
    var per = PAGE_CONTENT_MM * mm;
    if (!per || !host.clientHeight) { return; }
    var printing = !!(window.matchMedia && window.matchMedia('print').matches);

    // A hair of tolerance, or a page that fills exactly to its margin claims a
    // second, empty one.
    var pages = Math.max(1, Math.ceil((host.clientHeight - 2) / per));

    // On screen the sheet is one continuous block, so round it up to whole
    // pages. That keeps it looking like the stack of paper it will print as,
    // and it puts the signature block — which floats to the bottom — at the
    // foot of the last page rather than partway down it.
    // On paper the browser is already doing the pagination; growing the sheet
    // there would just add a blank page at the end.
    sheet.style.minHeight = printing ? '' : (pages * per + 28 * mm) + 'px';

    var total = host.clientHeight;
    for (var i = 0; i < pages; i++) {
      var tag = document.createElement('div');
      tag.className = 'inv-pageno';
      tag.textContent = 'Page ' + (i + 1) + ' of ' + pages;
      host.appendChild(tag);
      // Sit the tag's baseline just inside the bottom edge of each page's
      // content strip — measured from its own height, so it is never half cut
      // off by the edge — and never past where the content actually ends, or
      // the last one is stranded outside the sheet and never drawn.
      var h = tag.offsetHeight || 12;
      var y = (i + 1) * per - h - 1;
      tag.style.top = Math.max(0, Math.min(y, total - h - 1)) + 'px';
    }
  }

  // A sheet is usually laid out inside something not visible yet — the preview
  // pane behind a tab, the mobile preview before it slides up. Measured while
  // hidden every width is zero, so the fit is skipped; nothing then fires when
  // the container is finally shown and the page is left at full 210mm inside a
  // phone-width frame, overflowing to the right with the summary off-screen.
  // Watching the box it sits in catches that moment and every later resize.
  //
  // Wired from fitAll rather than on load: this script is emitted ahead of the
  // sheet markup, so at the time it first runs there is no sheet to observe.
  var ro = null;
  function observeSheets(sheets) {
    if (!window.ResizeObserver) { return; }
    if (!ro) {
      ro = new ResizeObserver(function (entries) {
        // Width is what drives the scale. Refitting on a height change would
        // chase the height the fit itself just produced.
        var moved = false;
        for (var i = 0; i < entries.length; i++) {
          var el = entries[i].target;
          var w = el.clientWidth;
          if (el.__invLastW !== w) { el.__invLastW = w; moved = true; }
        }
        if (!moved) { return; }
        clearTimeout(window.__invFitT);
        window.__invFitT = setTimeout(fitAll, 60);
      });
    }
    for (var s = 0; s < sheets.length; s++) {
      var box = sheets[s].parentElement;
      if (box && !box.__invObserved) {
        box.__invObserved = true;
        box.__invLastW = box.clientWidth;
        ro.observe(box);
      }
    }
  }

  function fitAll() {
    var sheets = document.querySelectorAll('.inv-sheet');
    observeSheets(sheets);
    for (var s = 0; s < sheets.length; s++) {
      // Measure at true size: a scale left over from the last pass would make
      // the sheet look narrower than A4 and shrink the type for no reason.
      unscale(sheets[s]);
      // Type first — the table has to fit the sheet's own 182mm whatever size
      // the sheet is being displayed at.
      var tables = sheets[s].querySelectorAll('table.inv-items');
      for (var t = 0; t < tables.length; t++) { fitOne(sheets[s], tables[t]); }
      // Then count pages, which depends on the height the fitted type produced.
      stampPages(sheets[s]);
      scaleSheet(sheets[s]);
    }
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', fitAll);
  } else {
    fitAll();
  }
  window.addEventListener('load', fitAll);
  // ResizeObserver is the right tool for ""the pane just became visible"", but it
  // is suspended while a tab is not rendering and absent on older engines. A
  // few settling passes cover the common case — a preview revealed a moment
  // after load — without depending on it. Bounded, so this cannot become a
  // polling loop.
  var settleAt = [200, 600, 1500];
  for (var q = 0; q < settleAt.length; q++) { setTimeout(fitAll, settleAt[q]); }
  // Print geometry differs from screen geometry, so measure again for paper.
  if (window.matchMedia) {
    try { window.matchMedia('print').addListener(fitAll); } catch (e) {}
  }
  window.addEventListener('beforeprint', fitAll);
  window.addEventListener('resize', function () {
    clearTimeout(window.__invFitT);
    window.__invFitT = setTimeout(fitAll, 120);
  });

  window.invoiceFitSheets = fitAll;
})();
</script>";

    /// <summary>strip_shared_sheet_assets: later documents of a bulk print drop the repeated assets.</summary>
    public static string Strip(string? html) => string.IsNullOrEmpty(html) ? html ?? "" : html.Replace(Css, "").Replace(FitJs, "");
}
