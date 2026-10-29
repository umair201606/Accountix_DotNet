/* Document form UI layer — sales and purchase invoices.
 *
 * Loaded after each form's own script, configured by window.DOC (set inline
 * by the template, because it carries URLs and permissions). It replaces or
 * wraps a few of the form's globals — rebuildActionBar, saveInvoice,
 * unapproveInvoice, markDirty, addRow, recalc — and keeps every element id
 * the rest of the form and the E2E suite address.
 *
 * States: new (never saved) · draft (saved, editable) · approved (locked).
 * - A saved draft stays editable. It used to freeze every field after the
 *   first save until the page was reloaded.
 * - Save is lit only when there is something to save.
 * - Approve and unapprove reload the page, so the locked/unlocked state, the
 *   lock notice and the FBR buttons always come from the server.
 * - One primary action per state; rare actions live in a "More" menu; on a
 *   phone the bar is pinned to the bottom of the screen.
 * - Forms without their own shortcuts get Ctrl+S / Ctrl+Shift+S / Ctrl+P /
 *   Alt+N / Alt+C, an unsaved-changes guard and Esc layering.
 */
(function () {
  'use strict';
  var DOC = window.DOC || {};
  function $(s, c) { return (c || document).querySelector(s); }
  function $$(s, c) { return (c || document).querySelectorAll(s); }
  function viewUrl(id) { return DOC.viewBase + id; }

  // ── shortcuts / dirty guard for forms that have none ──────────────────
  if (typeof window.leaveTo !== 'function') {
    window.formDirty = false;
    window.leaveTo = function (dest) {
      if (window.formDirty && DOC.editable) {
        showConfirm('Discard unsaved changes?', 'This document has changes that were not saved. Leave anyway?',
          function () { window.formDirty = false; window.location.href = dest; });
        return;
      }
      window.location.href = dest;
    };
    window.doSave = function () {
      if (!DOC.editable) { toast('Approved documents are locked — unapprove to edit', 'info'); return; }
      var b = $('#saveBtn');
      if (b && !b.disabled) b.click(); else toast('Nothing to save', 'info');
    };
    window.doSaveApprove = function () {
      if (!DOC.editable) { toast('Already approved', 'info'); return; }
      var b = $('#saveApproveBtn');
      if (!b) { toast('Approval rights required', 'warning'); return; }
      if (!b.disabled) b.click();
    };
    window.doPrint = function () {
      if (window.formDirty && DOC.editable) { toast('Save your changes before printing', 'warning'); return; }
      if (!$('#invoiceId').value) { toast('Save the document before printing', 'warning'); return; }
      window.print();
    };
    window.escapeLayer = function () {
      var pop = $('#chgPopover'); if (pop && pop.classList.contains('show') && window.closeChgPopover) { closeChgPopover(); return; }
      var mods = [['chargesModal', 'closeChargesModal'], ['orderModal', 'closeOrderModal'], ['confirmModal', 'closeConfirm']];
      for (var i = 0; i < mods.length; i++) {
        var m = document.getElementById(mods[i][0]);
        if (m && m.classList.contains('show') && typeof window[mods[i][1]] === 'function') { window[mods[i][1]](); return; }
      }
      var panel = $('#sidePanel'); if (panel && panel.classList.contains('open') && window.closeSidePanel) { closeSidePanel(); return; }
      var dd = $('.ac-dd.show'); if (dd) dd.classList.remove('show');
    };
    var inForm = function (el) {
      return !!(el && el.closest && (el.closest('.hdr-strip') || el.closest('#itemsBody') || el.closest('.btm') ||
        el.closest('#sidePanel') || el.closest('.ga') || el.closest('#chargesModal')));
    };
    document.addEventListener('input', function (e) {
      var t = e.target; if (!inForm(t) || t.closest('.ac-dd')) return;
      if (/Search$/.test(t.id || '')) return;   // raw typing in a picker is not yet document state
      window.markDirty();
    }, true);
    document.addEventListener('change', function (e) { if (inForm(e.target)) window.markDirty(); }, true);
    document.addEventListener('click', function (e) {
      var a = e.target.closest && e.target.closest('#cancelBtn,#newBtn');
      if (!a || !(window.formDirty && DOC.editable)) return;
      e.preventDefault(); window.leaveTo(a.id === 'newBtn' ? DOC.newUrl : DOC.listUrl);
    });
    document.addEventListener('keydown', function (e) {
      var k = (e.key || '').toLowerCase(), mod = e.ctrlKey || e.metaKey;
      if (mod && !e.altKey && !e.shiftKey && (k === 's' || k === 'p')) { e.preventDefault(); if (k === 's') doSave(); else doPrint(); return; }
      if (mod && e.shiftKey && !e.altKey && k === 's') { e.preventDefault(); doSaveApprove(); return; }
      if (e.altKey && !mod && (k === 'n' || k === 'c')) { e.preventDefault(); leaveTo(k === 'n' ? DOC.newUrl : DOC.listUrl); return; }
      if (e.key === 'Escape' && !mod && !e.altKey && !e.shiftKey) escapeLayer();
    });
    window.addEventListener('beforeunload', function (e) {
      if (window.formDirty && DOC.editable) { e.preventDefault(); e.returnValue = ''; }
    });
  }

  // ── flash message carried across a reload ─────────────────────────────
  try {
    var f = sessionStorage.getItem('docFlash');
    if (f) { sessionStorage.removeItem('docFlash'); var o = JSON.parse(f); setTimeout(function () { toast(o.m, o.t || 'success'); }, 50); }
  } catch (e) { /* storage blocked: the page still works */ }
  function flashThenGo(msg, url) {
    try { sessionStorage.setItem('docFlash', JSON.stringify({ m: msg, t: 'success' })); } catch (e) { }
    window.formDirty = false; window.location.replace(url);
  }

  // ── action bar ────────────────────────────────────────────────────────
  function btn(cls, id, label, title) { return '<button type="button" class="btn ' + cls + '"' + (id ? ' id="' + id + '"' : '') + (title ? ' title="' + title + '"' : '') + '>' + label + '</button>'; }
  function link(cls, id, label, href, title) { return '<a class="btn ' + cls + '"' + (id ? ' id="' + id + '"' : '') + ' href="' + href + '"' + (title ? ' title="' + title + '"' : '') + '>' + label + '</a>'; }
  function mi(label, o) {
    o = o || {};
    var a = ' role="menuitem"' + (o.id ? ' id="' + o.id + '"' : '') + (o.act ? ' data-act="' + o.act + '"' : '') + ' class="' + (o.cls || '') + '"';
    var hint = o.hint ? '<span class="hint">' + o.hint + '</span>' : '';
    return o.href ? '<a' + a + ' href="' + o.href + '">' + label + hint + '</a>' : '<button type="button"' + a + '>' + label + hint + '</button>';
  }
  function menu(items) {
    return '<div class="ab-more"><button type="button" class="btn btn-o ab-more-btn" aria-haspopup="menu" aria-expanded="false">More <span aria-hidden="true">&#9662;</span></button>' +
      '<div class="ab-menu" role="menu">' + items.join('') + '</div></div>';
  }
  function closeMenus() {
    $$('.ab-more.open').forEach(function (m) { m.classList.remove('open'); var b = m.querySelector('.ab-more-btn'); if (b) b.setAttribute('aria-expanded', 'false'); });
  }
  window.closeDocMenus = closeMenus;
  document.addEventListener('click', function (e) { if (!e.target.closest || !e.target.closest('.ab-more')) closeMenus(); });
  if (typeof window.escapeLayer === 'function') {
    var prevEsc = window.escapeLayer;
    window.escapeLayer = function () { if ($('.ab-more.open')) { closeMenus(); return; } prevEsc(); };
  }

  window.rebuildActionBar = function (st) {
    var bar = $('.ab'); if (!bar) return;
    if (st === 'saved' || st === 'unapproved') st = 'draft';
    bar.dataset.state = st;
    var h = [], noun = DOC.noun || 'invoice';
    if (st === 'new') {
      h.push(link('btn-o', 'cancelBtn', 'Cancel', DOC.listUrl, 'Back to the list without saving (Alt+C)'));
      h.push(btn('btn-p', 'saveBtn', 'Save draft', 'Save as a draft (Ctrl+S)'));
      if (DOC.canApprove) h.push(btn('btn-s', 'saveApproveBtn', 'Save &amp; approve', 'Save, post to the ledger and lock (Ctrl+Shift+S)'));
    } else if (st === 'draft') {
      h.push(menu([mi('New ' + noun, { id: 'newBtn', href: DOC.newUrl, hint: 'Alt+N' }),
        mi('Print', { act: 'print', cls: 'only-sm', hint: 'Ctrl+P' }),
        mi('Back to list', { act: 'back', cls: 'only-sm', hint: 'Alt+C' }),
        '<hr>', mi('Delete draft', { id: 'deleteBtn', cls: 'danger' })]));
      h.push(link('btn-o hide-sm', 'cancelBtn', 'Back to list', DOC.listUrl, 'Back to the list (Alt+C)'));
      h.push(btn('btn-o hide-sm', 'printBtn', 'Print', 'Print (Ctrl+P)'));
      h.push(btn('btn-p is-clean', 'saveBtn', 'Save', 'Save changes (Ctrl+S)'));
      if (DOC.canApprove) h.push(btn('btn-s', 'saveApproveBtn', 'Approve', 'Save, post to the ledger and lock (Ctrl+Shift+S)'));
    } else {
      var mm = [mi('New ' + noun, { id: 'newBtn', href: DOC.newUrl, hint: 'Alt+N' }),
        mi('Back to list', { act: 'back', cls: 'only-sm', hint: 'Alt+C' })];
      if (DOC.fbr) mm.push(mi('Validate with FBR', { id: 'validateFbrBtn' }));
      if (DOC.canApprove) { mm.push('<hr>'); mm.push(mi('Unapprove', { id: 'unapproveBtn', cls: 'danger', hint: 'reverses the posting' })); }
      h.push(menu(mm));
      h.push(link('btn-o hide-sm', 'cancelBtn', 'Back to list', DOC.listUrl, 'Back to the list (Alt+C)'));
      if (DOC.fbr) h.push(btn('btn-o', 'submitFbrBtn', 'Send to FBR'));
      h.push(btn('btn-s', 'printBtn', 'Print', 'Print (Ctrl+P)'));
    }
    bar.innerHTML = h.join('');
    var sb = $('#saveBtn');
    if (sb) { sb.addEventListener('click', function () { saveInvoice('save'); }); if (st === 'draft') sb.disabled = !window.formDirty; }
    var sa = $('#saveApproveBtn');
    if (sa) sa.addEventListener('click', function () {
      showConfirm(st === 'new' ? 'Save & Approve' : 'Approve ' + noun, DOC.approveMsg, function () { saveInvoice('approve'); });
    });
    var pb = $('#printBtn'); if (pb) pb.addEventListener('click', function () { doPrint(); });
    var db = $('#deleteBtn');
    if (db) db.addEventListener('click', function () { closeMenus(); showConfirm('Delete draft', 'Delete this draft ' + noun + '? This cannot be undone.', deleteInvoice); });
    var ub = $('#unapproveBtn');
    if (ub) ub.addEventListener('click', function () { closeMenus(); showConfirm('Unapprove ' + noun, DOC.unapproveMsg, unapproveInvoice); });
    if (DOC.fbr && typeof window.wireFbrBtns === 'function') wireFbrBtns();
    $$('.ab [data-act]').forEach(function (el) {
      el.addEventListener('click', function () {
        closeMenus();
        if (el.dataset.act === 'print') doPrint(); else if (el.dataset.act === 'back') leaveTo(DOC.listUrl);
      });
    });
    var mb = $('.ab-more-btn');
    if (mb) mb.addEventListener('click', function (e) {
      e.stopPropagation();
      var box = mb.parentElement, open = !box.classList.contains('open');
      closeMenus(); box.classList.toggle('open', open); mb.setAttribute('aria-expanded', open ? 'true' : 'false');
      if (open) { var first = box.querySelector('.ab-menu [role=menuitem]'); if (first) first.focus(); }
    });
  };

  // Save lights up only when there is something to save.
  window.markDirty = function () {
    window.formDirty = true;
    var b = $('#saveBtn'), bar = $('.ab');
    if (b && bar && bar.dataset.state === 'draft') { b.disabled = false; b.classList.remove('is-clean'); }
  };

  window.saveInvoice = async function (action) {
    var data;
    try { data = collectData(); } catch (e) { toast('Form error: ' + e.message, 'error'); return; }
    if (!data[DOC.partyKey]) { toast('Please select a ' + DOC.partyNoun, 'error'); var ps = document.getElementById(DOC.partySearch); if (ps) ps.focus(); return; }
    if (!data.items || data.items.length === 0) { toast('Add at least one item', 'error'); return; }
    data.action = action;
    var btns = $$('.ab .btn'); btns.forEach(function (b) { b.disabled = true; });
    var res;
    try {
      var r = await fetch(DOC.saveUrl, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
      res = await r.json();
    } catch (e) { toast('Network or server error: ' + e.message, 'error'); btns.forEach(function (b) { b.disabled = false; }); return; }
    if (!res.ok) { toast(res.error || 'Save failed', 'error'); btns.forEach(function (b) { b.disabled = false; }); return; }
    var status = res.voucher_status || res.status, number = res.number || res.voucher;
    $('#invoiceId').value = res.id;
    if (res.number && $('#invNumber')) $('#invNumber').value = res.number;
    if (res.voucher && $('#invVoucher')) $('#invVoucher').value = res.voucher;
    if (status === 'approved') { flashThenGo(res.message, viewUrl(res.id)); return; }
    toast(res.message, 'success');
    var t = $('#docTitle'); if (t) t.textContent = number;
    document.title = number + ' · ' + DOC.title;
    try { history.replaceState(null, '', viewUrl(res.id)); } catch (e) { }
    var badge = $('#statusBadge'); if (badge) { badge.className = 'bdg bdg-unapproved'; badge.textContent = 'Draft'; }
    window.formDirty = false;
    rebuildActionBar('draft');
    if (typeof recalc === 'function') recalc();
  };

  window.unapproveInvoice = async function () {
    var id = $('#invoiceId').value; if (!id) return;
    var mb = $('.ab-more-btn'); if (mb) { mb.disabled = true; mb.textContent = 'Unapproving…'; }
    var res;
    try { var r = await fetch(DOC.unapproveBase + id, { method: 'POST' }); res = await r.json(); } catch (e) { res = { ok: false, error: e.message }; }
    if (!res.ok) { toast(res.error || 'Unapprove failed', 'error'); if (mb) { mb.disabled = false; mb.textContent = 'More'; } return; }
    flashThenGo(res.message || 'Unapproved', viewUrl(id));
  };

  // ── payment terms → due date (forms that have both) ───────────────────
  (function () {
    var sel = $('#payTerms'), inv = $('#invDate'), due = $('#dueDate'); if (!sel || !inv || !due) return;
    function addDays(iso, n) {
      var d = new Date(iso + 'T00:00:00'); if (isNaN(d)) return '';
      d.setDate(d.getDate() + n);
      return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
    }
    function apply() { if (sel.value === '' || !inv.value) return; due.value = addDays(inv.value, parseInt(sel.value, 10)); }
    sel.addEventListener('change', apply);
    inv.addEventListener('change', apply);
    due.addEventListener('change', function () {
      if (sel.value !== '' && due.value !== addDays(inv.value, parseInt(sel.value, 10))) sel.value = '';
    });
    if (inv.value && due.value) {
      var diff = Math.round((new Date(due.value) - new Date(inv.value)) / 864e5);
      sel.value = sel.querySelector('option[value="' + diff + '"]') ? String(diff) : '';
    }
  })();

  // ── line items: phone card labels; descriptions that never clip ──────
  function labelRows() {
    var ths = $$('#itemsTable thead th');
    $$('#itemsBody tr').forEach(function (tr) {
      Array.prototype.forEach.call(tr.children, function (td, i) {
        var th = ths[i]; if (!th) return;
        var t = (th.textContent || '').trim();
        if (t && t !== '#') td.dataset.label = t;
        if (th.classList.contains('desc-col')) td.classList.add('c-desc');
        if (t === 'Code') td.classList.add('c-code');
        if (td.querySelector('.ra')) td.classList.add('c-act');
      });
    });
  }
  function fitDesc() {
    $$('#itemsBody .ac-desc-input').forEach(function (ta) {
      ta.style.height = 'auto'; var h = ta.scrollHeight; if (h > 0) ta.style.height = h + 'px';
    });
  }
  if (typeof window.addRow === 'function') {
    var _ar = window.addRow;
    window.addRow = function () { var r = _ar.apply(this, arguments); labelRows(); fitDesc(); return r; };
  }
  if (typeof window.recalc === 'function') {
    var _rc = window.recalc;
    window.recalc = function () { var r = _rc.apply(this, arguments); fitDesc(); return r; };
  }
  var fitT;
  window.addEventListener('resize', function () { clearTimeout(fitT); fitT = setTimeout(fitDesc, 120); });
  labelRows(); setTimeout(fitDesc, 0); setTimeout(fitDesc, 700);

  // ── read-only document: empty fields read "—" ────────────────────────
  if ($('#docWrap.is-locked')) {
    $$('.hdr-strip input:disabled').forEach(function (el) {
      if ((el.type === 'date' || el.type === 'time') && !el.value) { el.type = 'text'; el.value = '—'; }
      else if (!el.value && el.type !== 'hidden') el.placeholder = '—';
    });
    var pt = $('#payTerms');
    if (pt && pt.value === '') { var o0 = pt.querySelector('option[value=""]'); if (o0) o0.textContent = '—'; }
  }

  // ── plain language: options panel, mode note, charge editor ──────────
  // The calculation code names its modes "Combined / Per line / Per item"
  // and its sections "1 · Discount". People think "whole invoice / each
  // line". Text only — values, ids and data-value attributes are untouched.
  var WORDS = [
    [/\bCombined\b/g, 'Whole invoice'], [/\bPer line\b/g, 'Each line'],
    [/\bPer item\b/g, 'Spread over lines'], [/\bper item\b/g, 'spread over lines'],
    [/\bW\/H\b/g, 'Withholding'], [/\bOn\b/g, 'on'], [/\bOff\b/g, 'off']
  ];
  function plain(t) { WORDS.forEach(function (w) { t = t.replace(w[0], w[1]); }); return t; }
  var LABELS = {
    'Scope': 'Apply to', 'Method': 'Enter discount as', 'Sales tax scope': 'Apply sales tax to',
    'Sales Tax % (combined)': 'Sales tax rate (%)', 'Further Tax (sales only)': 'Further tax',
    'Further Tax %': 'Further tax rate (%)', 'Withholding Tax': 'Withholding tax', 'WHT %': 'Withholding rate (%)',
    'Default scope for new charges': 'New charges apply to', 'Default distribution (per-item charges)': 'Spread new charges by',
    'Default tax bases for billed charges': 'Taxes on new billed charges', 'Invoice Template': 'Print design', 'Copies': 'Copies to print',
    'Input tax scope': 'Apply input tax to', 'Input Tax % (combined)': 'Input tax rate (%)',
    'Carriage columns (commission / freight / loading)': 'Commission, freight and loading'
  };
  var SECTION_HELP = {
    'Discount': 'Give one discount on the whole invoice, or a discount on each line.',
    'Additional charges': 'Defaults for freight, installation and other charges you add.',
    'Taxes': 'Sales tax for the whole invoice or line by line, plus further and withholding tax.',
    'Expenses': 'Commission, freight and loading on the whole bill or on each line.',
    'Columns & fields': 'Extra columns and what the printed copy looks like.'
  };
  function relabelPanel() {
    $$('#sidePanel .acc-h > span:first-child').forEach(function (sp) {
      if (sp.dataset.plain) return;
      var t = sp.textContent.replace(/^\s*\d+\s*·\s*/, '').trim();
      sp.textContent = t; sp.dataset.plain = '1';
      var help = SECTION_HELP[t] || SECTION_HELP[t.replace(/^Additional /, 'Additional ')];
      var body = sp.closest('.accordion') && sp.closest('.accordion').querySelector('.acc-b');
      if (help && body && !body.querySelector('.acc-help')) {
        var p = document.createElement('p'); p.className = 'acc-help'; p.textContent = help; body.insertBefore(p, body.firstChild);
      }
    });
    $$('#sidePanel .lbl').forEach(function (l) { var t = l.textContent.trim(); if (LABELS[t]) l.textContent = LABELS[t]; });
    $$('#sidePanel .pill-b').forEach(function (b) { b.textContent = plain(b.textContent); });
    $$('#sidePanel .acc-sum').forEach(function (s) { s.textContent = plain(s.textContent); });
    var dn = $('#discMethodNote'); if (dn) dn.textContent = plain(dn.textContent).replace('below the table', 'in the summary').replace('the total is a rollup', 'the summary adds them up');
    $$('#sidePanel select option').forEach(function (o) { if (/^(Combined|Per item)$/.test(o.textContent.trim())) o.textContent = plain(o.textContent); });
  }
  // Mode note above the grid: say only what differs from the default.
  function modeNote() {
    var box = $('#modeChips'); if (!box) return;
    var on = [];
    $$('#modeChips .chip:not(.chip-off)').forEach(function (c) {
      var k = (c.querySelector('.chip-k') || {}).textContent || '';
      var v = c.textContent.replace(k, '').trim();
      on.push(plain(k.charAt(0) + k.slice(1).toLowerCase() + ': ' + v));
    });
    var note = $('#modeNote');
    if (!note) { note = document.createElement('button'); note.type = 'button'; note.id = 'modeNote'; note.className = 'mode-note';
      note.addEventListener('click', function () { if (window.openSidePanel) openSidePanel(); });
      box.parentNode.insertBefore(note, box); }
    note.textContent = on.length ? on.join(' · ') : '';
    note.hidden = !on.length;
    note.title = 'Change in Invoice options';
  }
  ['updatePanelSummaries', 'renderChips'].forEach(function (fn) {
    if (typeof window[fn] !== 'function') return;
    var orig = window[fn];
    window[fn] = function () { var r = orig.apply(this, arguments); relabelPanel(); modeNote(); return r; };
  });
  relabelPanel(); modeNote();

  // Charge editor: the treatment is a choice between three plain outcomes,
  // shown as cards, not a select of accounting jargon. The select stays (hidden)
  // and still drives the existing handler, so nothing about saving changes.
  var TREAT = DOC.kind === 'purchase' ? {
    bill: ['Supplier bills it', 'A separate charge on this bill, added to what you owe.'],
    absorb: ['Add to stock cost', 'Spread into the cost of the items received, like carriage inward.'],
    expense: ['We bear it', 'Posts to the expense ledger only; stock cost is unchanged.']
  } : {
    bill: ['Bill the customer', 'Printed on the invoice and added to the total.'],
    absorb: ['Add to item prices', 'Spread into the line amounts; nothing separate prints.'],
    expense: ['Company expense', 'The customer is not charged; posts to the expense ledger only.']
  };
  var CHG_LABELS = {
    'Charge type — expense ledger': 'Charge account', 'Amount': 'Amount', 'Scope': 'Apply to',
    'Distribution across lines': 'How to spread it', 'Treatment': 'Who pays', 'Include in tax base': 'Taxes on this charge'
  };
  function enhanceCharges() {
    $$('#chargesList .chg-card').forEach(function (card, idx) {
      card.querySelectorAll('.chg-lbl').forEach(function (l) { var t = l.textContent.trim(); if (CHG_LABELS[t]) l.textContent = CHG_LABELS[t]; });
      card.querySelectorAll('.chg-checks span').forEach(function (sp) { sp.textContent = sp.textContent.replace('W/H tax', 'Withholding tax'); });
      card.querySelectorAll('.chg-scope').forEach(function (b) { b.textContent = b.dataset.v === 'individual' ? 'Spread over lines' : 'Whole invoice'; });
      var sel = card.querySelector('.chg-treatment');
      if (sel && !card.querySelector('.chg-treat')) {
        var wrap = document.createElement('div'); wrap.className = 'chg-treat'; wrap.setAttribute('role', 'radiogroup');
        wrap.setAttribute('aria-label', 'Who pays for this charge');
        Array.prototype.forEach.call(sel.options, function (o) {
          var t = TREAT[o.value] || [o.textContent, ''];
          var lab = document.createElement('label'); lab.className = 'chg-treat-opt' + (sel.value === o.value ? ' on' : '');
          lab.innerHTML = '<input type="radio" name="chgTreat' + idx + '" value="' + o.value + '"' + (sel.value === o.value ? ' checked' : '') + '>' +
            '<span class="ct-t"></span><span class="ct-d"></span>';
          lab.querySelector('.ct-t').textContent = t[0]; lab.querySelector('.ct-d').textContent = t[1];
          lab.querySelector('input').addEventListener('change', function () {
            sel.value = o.value; sel.dispatchEvent(new Event('change', { bubbles: true }));
          });
          wrap.appendChild(lab);
        });
        sel.style.display = 'none';
        sel.parentNode.appendChild(wrap);
        // Who pays is the first decision after what and how much: move it up.
        var bot = card.querySelector('.chg-bot'), top = card.querySelector('.chg-top');
        var treatBlock = sel.parentNode;
        if (bot && top) { var row = document.createElement('div'); row.className = 'chg-who'; row.appendChild(treatBlock); top.after(row); }
      }
      var none = card.querySelector('.chg-sec .chg-none');
      if (none && /single invoice-level/.test(none.textContent)) none.textContent = 'One amount for the whole invoice — choose “Spread over lines” to split it.';
    });
    var list = $('#chargesList');
    if (list && !list.children.length) {
      list.innerHTML = '<div class="chg-empty"><b>No charges yet</b><span>Freight, installation, packing — add one and choose who pays for it.</span></div>';
    }
  }
  if (typeof window.renderCharges === 'function') {
    var _rch = window.renderCharges;
    window.renderCharges = function () { var r = _rch.apply(this, arguments); enhanceCharges(); return r; };
  }

  // Summary rail quick links.
  var addC = $('#sumAddCharge');
  if (addC) {
    if (!DOC.editable) addC.parentNode.style.display = 'none';
    addC.addEventListener('click', function () {
      if (typeof openChargesModal !== 'function') return;
      openChargesModal();
      if (typeof charges !== 'undefined' && (!charges.length || charges.every(function (c) { return !(parseFloat(c.amount) > 0); })) &&
          typeof addChargeRow === 'function' && !charges.length) addChargeRow();
    });
  }
  var addT = $('#sumAddTax');
  if (addT) addT.addEventListener('click', function () {
    if (typeof openSidePanel !== 'function') return;
    openSidePanel();
    $$('#sidePanel .acc-h').forEach(function (h) {
      if (/Tax/.test(h.textContent) && !h.classList.contains('open') && typeof toggleAccordion === 'function') toggleAccordion(h);
    });
  });

  var bar0 = $('.ab');
  rebuildActionBar(bar0 ? bar0.dataset.state : 'new');
})();
