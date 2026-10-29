/* Accounting entry panel (partials/gl_entry.html).
 *
 * Opens a side panel showing the journal(s) the document has posted: each
 * account debited and credited, with totals and a balance check. When a
 * document carries more than one posted journal (a later cost
 * re-adjustment, a payment), a combined "net effect" table leads. Journals
 * that were reversed by unapproving are listed under history: they had no
 * effect on the books.
 */
(function () {
  'use strict';
  var fab = document.getElementById('gleFab');
  if (!fab) return;
  var panel = document.getElementById('glePanel'), overlay = document.getElementById('gleOverlay');
  var body = document.getElementById('gleBody'), sub = document.getElementById('gleSub');
  var lastFocus = null;

  function money(v) {
    if (typeof window.formatAmount === 'function') return window.formatAmount(v, 2);
    return Number(v || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function fmtDate(iso) {
    if (!iso) return '';
    var d = new Date(iso + 'T00:00:00');
    return isNaN(d) ? iso : d.toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' });
  }
  function docId() {
    var sel = fab.dataset.idFrom, v = '';
    if (sel) { var el = document.querySelector(sel); if (el) v = (el.value || '').trim(); }
    return v || fab.dataset.id || '';
  }
  function url() {
    return '/accounting/gl-entry?type=' + encodeURIComponent(fab.dataset.type) + '&id=' + encodeURIComponent(docId()) +
      (fab.dataset.also ? '&also=' + encodeURIComponent(fab.dataset.also) : '');
  }
  function ledgerHref(base, accountId, date) {
    if (!base) return null;
    var q = 'mode=select&selection_mode=custom&account_ids=' + accountId;
    if (date) q += '&filter_mode=custom&from=' + date + '&to=' + date;
    return base + '?' + q;
  }

  function rowsHtml(lines, base, date) {
    return lines.map(function (l) {
      var href = ledgerHref(base, l.account_id, date);
      var name = href ? '<a href="' + href + '" target="_blank" rel="noopener" title="Open this account in the general ledger">' + esc(l.name) + '</a>' : esc(l.name);
      var note = [l.description, l.label ? 'Label: ' + l.label : ''].filter(Boolean).join(' · ');
      return '<tr class="' + (l.credit > 0 && !(l.debit > 0) ? 'gle-cr' : 'gle-dr') + '">' +
        '<td><div class="gle-acc"><span class="gle-code">' + esc(l.code) + '</span>' + name +
        (note ? '<span class="gle-note">' + esc(note) + '</span>' : '') + '</div></td>' +
        '<td class="num">' + (l.debit > 0 ? money(l.debit) : '') + '</td>' +
        '<td class="num">' + (l.credit > 0 ? money(l.credit) : '') + '</td></tr>';
    }).join('');
  }
  function tableHtml(lines, dr, cr, base, date) {
    return '<table class="gle-tbl"><thead><tr><th>Account</th><th class="num">Debit</th><th class="num">Credit</th></tr></thead>' +
      '<tbody>' + rowsHtml(lines, base, date) + '</tbody>' +
      '<tfoot><tr class="gle-tot"><td>Total</td><td class="num">' + money(dr) + '</td><td class="num">' + money(cr) + '</td></tr></tfoot></table>' +
      (Math.abs(dr - cr) < 0.005
        ? '<div class="gle-bal">✓ Balanced — debits equal credits</div>'
        : '<div class="gle-bal bad">Out of balance by ' + money(Math.abs(dr - cr)) + '</div>');
  }
  function cardHtml(e, base, tag) {
    return '<section class="gle-card"><div class="gle-card-h"><div>' +
      '<div class="gle-card-t">' + esc(e.title) + ' · ' + esc(e.number) + '</div>' +
      '<div class="gle-card-m">' + esc(fmtDate(e.date)) + (e.description ? ' · ' + esc(e.description) : '') + '</div></div>' +
      (tag || '') + '</div>' + tableHtml(e.lines, e.total_debit, e.total_credit, base, e.date) + '</section>';
  }
  // Net effect across several posted journals, by account.
  function netHtml(entries, base) {
    var acc = {}, order = [];
    entries.forEach(function (e) {
      e.lines.forEach(function (l) {
        if (!acc[l.account_id]) { acc[l.account_id] = { account_id: l.account_id, code: l.code, name: l.name, net: 0 }; order.push(l.account_id); }
        acc[l.account_id].net += (l.debit || 0) - (l.credit || 0);
      });
    });
    var lines = order.map(function (id) {
      var a = acc[id];
      return { account_id: id, code: a.code, name: a.name, debit: a.net > 0.004 ? a.net : 0, credit: a.net < -0.004 ? -a.net : 0 };
    }).filter(function (l) { return l.debit || l.credit; })
      .sort(function (x, y) { return (x.credit > 0) - (y.credit > 0); });
    var dr = lines.reduce(function (s, l) { return s + l.debit; }, 0), cr = lines.reduce(function (s, l) { return s + l.credit; }, 0);
    return '<section class="gle-card"><div class="gle-card-h"><div><div class="gle-card-t">Net effect on the books</div>' +
      '<div class="gle-card-m">All posted journals for this document, combined by account</div></div></div>' +
      tableHtml(lines, dr, cr, base, null) + '</section>';
  }

  function render(d) {
    var posted = d.posted || [], hist = d.history || [], base = d.ledger_url, h = [];
    if (!d.saved) {
      sub.textContent = 'Not saved yet';
      h.push('<div class="gle-empty"><b>Nothing posted yet</b>This document has no effect on the general ledger until it is saved and approved. Approve it to post the entry.</div>');
    } else if (!posted.length) {
      sub.textContent = 'Not posted';
      h.push('<div class="gle-empty"><b>Not posted to the ledger</b>' +
        (hist.length ? 'It was posted before and then unapproved, so its entry was reversed (shown below). Approve it again to post.'
          : 'This draft has no effect on the books yet. Approving it posts its entry, which will then show here.') + '</div>');
    } else {
      var main = posted[0];
      sub.textContent = 'Posted ' + fmtDate(main.date) + ' · ' + posted.length + (posted.length === 1 ? ' journal' : ' journals');
      if (posted.length > 1) h.push(netHtml(posted, base));
      if (posted.length > 1) h.push('<div class="gle-sec-t">Journals</div>');
      posted.forEach(function (e) {
        var tag = e.kind === 'cost_adjustment' ? '<span class="gle-tag warn">Re-costed</span>'
          : e.kind === 'related' ? '<span class="gle-tag muted">Related</span>' : '<span class="gle-tag">Posted</span>';
        h.push(cardHtml(e, base, tag));
      });
    }
    if (hist.length) {
      h.push('<details class="gle-hist"><summary>Earlier versions — reversed, no effect on the books (' + hist.length + ')</summary>' +
        hist.map(function (e) { return cardHtml(e, null, '<span class="gle-tag muted">Reversed</span>'); }).join('') + '</details>');
    }
    body.innerHTML = h.join('');
    fab.dataset.state = posted.length ? 'posted' : 'unposted';
  }

  function load() {
    return fetch(url(), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
      .then(function (r) { return r.json(); });
  }
  function open() {
    lastFocus = document.activeElement;
    overlay.hidden = false; panel.hidden = false;
    sub.textContent = 'Loading…'; body.innerHTML = '';
    document.getElementById('gleClose').focus();
    // Nothing saved yet: nothing to fetch.
    if (!docId()) { render({ ok: true, saved: false, posted: [], history: [] }); return; }
    load().then(function (d) {
      if (!d.ok) { sub.textContent = ''; body.innerHTML = '<div class="gle-empty">' + esc(d.error || 'Could not load the entry') + '</div>'; return; }
      render(d);
    }).catch(function () { sub.textContent = ''; body.innerHTML = '<div class="gle-empty">Could not load the entry. Check your connection and try again.</div>'; });
  }
  function close() {
    overlay.hidden = true; panel.hidden = true;
    if (lastFocus && lastFocus.focus) lastFocus.focus();
  }
  window.closeGlEntry = close;

  fab.addEventListener('click', open);
  overlay.addEventListener('click', close);
  document.getElementById('gleClose').addEventListener('click', close);
  // Capture phase, so a form's own Esc handler doesn't also act on this press.
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !panel.hidden) { e.stopImmediatePropagation(); e.preventDefault(); close(); }
  }, true);

  // Status dot without opening: posted (filled) or not (hollow).
  if (docId()) {
    load().then(function (d) { if (d && d.ok) fab.dataset.state = (d.posted || []).length ? 'posted' : 'unposted'; }).catch(function () {});
  } else {
    fab.dataset.state = 'unposted';
  }
})();
