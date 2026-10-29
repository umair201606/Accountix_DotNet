/* Light / dark theme switch, shared by the app shell and every standalone page
 * (portal, hub, super admin console, sign-in, landing).
 *
 * The theme follows the OS until the user picks one; the pick is stored in
 * localStorage("ax-theme") and stamped on <html data-theme>. A tiny inline
 * script in <head> (partials/_v2_head.html, app_shell.html) applies the
 * stored pick before first paint; this file wires the buttons.
 *
 * The old handler flipped on the data-theme attribute alone. With the OS in
 * dark mode and nothing stored, the attribute is absent, so the first click
 * "switched" to dark — which was already showing — and nothing happened.
 * The current theme is now read as the user sees it.
 */
(function () {
  var root = document.documentElement;
  var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

  function effective() {
    var t = root.getAttribute('data-theme');
    if (t === 'dark' || t === 'light') return t;
    return media && media.matches ? 'dark' : 'light';
  }
  function sync() {
    var cur = effective();
    root.setAttribute('data-theme-effective', cur);
    document.querySelectorAll('[data-theme-toggle]').forEach(function (b) {
      var next = cur === 'dark' ? 'light' : 'dark';
      b.setAttribute('aria-pressed', cur === 'dark' ? 'true' : 'false');
      b.setAttribute('aria-label', 'Switch to ' + next + ' theme');
      b.title = 'Switch to ' + next + ' theme';
    });
  }
  function toggle() {
    var next = effective() === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('ax-theme', next); } catch (e) { /* private mode: this page only */ }
    sync();
  }
  window.axToggleTheme = toggle;
  document.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-theme-toggle]');
    if (b) { e.preventDefault(); toggle(); }
  });
  if (media && media.addEventListener) media.addEventListener('change', sync);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', sync); else sync();
})();
