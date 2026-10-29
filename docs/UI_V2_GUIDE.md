# UI v2 — design guide

The visual system this ERP runs on, and the procedure for bringing a page onto
it. Every module the app shell serves — invoicing, inventory, HR, executive,
FBR, fixed assets, settings, and now finance and accounting — is on it, and so
are the pages that bypass the shell entirely: the landing page, both sign-in
screens, the company portal, the module hub and the super admin console (§2).

`DESIGN.md` documents the older standard and is kept for one reason: a module
added later starts on v1 until someone converts its palette, and rolling a
module back is still one line. Where the two disagree, this file wins for pages
inside a module listed in `UI_V2_MODULES`, and `DESIGN.md` wins everywhere else.

Everything here is implemented in `static/css/ui.css`. Read that file
alongside this one — it carries the reasoning for individual rules, this
carries the system.

---

## 1. The governing idea

**Almost monochrome. Colour is a signal, not decoration.**

A screen is neutral ink on neutral surfaces, with one accent that means
something wherever it appears. If you find yourself picking a colour because a
tile "needed" one, that tile needed a better label instead.

Three consequences worth stating outright, because they are what most changes
get wrong:

- **The rail is light, not a navy slab.** The sidebar is the same white as a
  card. It is navigation, not the subject of the page.
- **Sections, not cards-in-cards.** A bordered rounded box around every group
  adds edges. One hairline and real whitespace separate things better.
- **Numbers are the loudest thing on screen.** Nothing else competes: no
  saturated fills, no heavy dividers, no coloured body text.

---

## 2. Turning a module on

`templates/layouts/app_shell.html` holds the gate:

```jinja
{% set UI_V2_MODULES = ['invoicing', 'inventory', 'hr', 'executive',
                        'fbr', 'fixed_assets', 'settings',
                        'finance', 'accounting'] %}
```

Add a module key to opt it in; remove it to roll back. The shell then stamps
`data-ui="v2"` on `<html>`, and every rule in `ui.css` is scoped to that
attribute, so no other module's chrome moves.

A page that renders **outside the shell** does not inherit the stamp and must
repeat it itself: `<html data-ui="v2">`, the two stylesheet links, and the
`ax-theme` bootstrap script. Miss it and the page does not merely fall back to
v1 — every `--v2-*` name it uses becomes undefined, which invalidates the whole
declaration and strips the styling (see §3).

There are two kinds. **Print and preview popups** open in their own window and
inline the opt-in by hand, because they deliberately load nothing else:
`accounting/voucher_preview.html` and `inventory/vouchers/voucher_preview.html`
are the worked examples. **Full pages that bypass the shell** — the landing
page, both sign-in screens, the company portal, the module hub and the super
admin console — include `partials/_v2_head.html` instead, which carries the
theme bootstrap, Inter, `app.css` and `ui.css` in one line:

```jinja
<html lang="en" data-ui="v2">
<head>
  ...
  {% include "partials/_v2_head.html" %}
  <style> /* page-specific layout; colour comes from tokens */ </style>
```

Include it **before** the page's own `<style>`, so the page wins the
equal-specificity tie against `ui.css`'s bridge blocks (both `(0,1,0)`; later
wins). These pages sat on the navy `DESIGN.md` palette long after every module
had moved, each with a private `:root` of hardcoded light values — nine copies
of one light theme that no test could see. `tests/unit/test_standalone_page_v2.py`
now discovers every template that opens its own `<html>` and fails if it lacks
the stamp, never loads `ui.css`, or hardcodes a colour outside a `var()`
fallback.

Specificity: `[data-ui="v2"] .thing` is (0,2,0) and beats a plain `.thing`
(0,1,0) from `app.css` regardless of source order. A template's own
`.thing` also loses to it — but `.thing.thing` or `.wrap .thing` (also (0,2,0))
**wins**, because the template's `<style>` is parsed after the stylesheet
`<link>`. That tie-break is deliberate and is how the two invoice forms keep
their bespoke button copies. Know which side of it you are on before adding a
rule.

Before opting a module in, check it does not ship a light-only `:root` palette
or reach for Tailwind's colour utilities (`text-slate-400`, `text-red-600`) —
both are fixed light values that keep their colour when the text around them
turns light. See §7.

---

## 3. Tokens

Declared on `[data-ui="v2"]` (not `:root`, so a template's own `:root` block
cannot silently win on equal specificity). **Never hardcode a hex in a
template**; reach for a token.

### Ink and surface

| Token | Light | Dark | Use |
|---|---|---|---|
| `--v2-ink` | `#161616` | `#F0F0EF` | headings, values, primary text |
| `--v2-ink-2` | `#2E2E2E` | `#D4D4D2` | hover state of ink fills |
| `--v2-body` | `#4A4A48` | `#A5A5A2` | body copy, table cells |
| `--v2-muted` | `#7C7C7A` | `#82827F` | labels, axis text, captions |
| `--v2-faint` | `#848482` | `#74746F` | metadata, the quietest text |
| `--v2-ground` | `#F5F5F5` | `#0E0E0E` | the page behind the cards |
| `--v2-surface` | `#FFFFFF` | `#171717` | cards, tables, popovers |
| `--v2-rail` | `#FFFFFF` | `#121212` | sidebar |
| `--v2-hover` | `#F0F0EF` | `#1D1D1D` | row/nav hover |
| `--v2-sunk` | `#E9E9E7` | `#242424` | active nav pill, progress track |
| `--v2-line` | `#E8E8E6` | `#242424` | hairlines, card borders |
| `--v2-line-2` | `#DCDCDA` | `#2E2E2E` | input borders, stronger rules |

### Meaning

| Token | Light | Dark | Means |
|---|---|---|---|
| `--v2-accent` | `#2B7263` | `#6FB3A2` | links, focus, the one accent |
| `--v2-accent-ink` | `#275650` | `#8CC7B8` | accent text on `accent-soft` |
| `--v2-accent-soft` | `#E8F4F2` | `#16302B` | accent-tinted fills |
| `--v2-pos` / `-bg` | `#2B7263` / `#E8F4F2` | `#6FB3A2` / `#16302B` | good, up, approved |
| `--v2-warn` / `-bg` | `#86794F` / `#F7F2E4` | `#C4B075` / `#282316` | attention, down, pending |
| `--v2-neg` / `-bg` | `#B4453B` / `#FBECEA` | `#E08579` / `#2C1613` | error, destructive |

Positive and accent are the **same hue on purpose**. A dashboard where "good"
and "the brand" are different greens spends two colours on one idea.

### Legacy bridge

`ui.css` re-points the older names every existing rule already reads — `--bg`,
`--card`, `--border`, `--text`, `--muted`, `--light`, `--primary`, `--accent`,
`--success`, `--danger`, `--warning`, `--surface-low`, `--surface-variant`,
`--radius`, `--shadow`, `--shadow-lg` — at the v2 tokens. That is what carries
the palette into markup `ui.css` never mentions.

**An undefined custom property invalidates the entire declaration** — it does
not fall back to an inherited value. A page reaching for a name nothing
defines silently loses that whole rule (this is exactly how a progress bar lost
its track and a popover its shadow). If you use a name, confirm the bridge
defines it.

---

## 4. Type and space

| Role | Size | Weight | Notes |
|---|---|---|---|
| Page title | 21px | 600 | `-0.02em` tracking |
| Section title | 13.5px | 600 | sentence case, never uppercase |
| Stat value | 29px | 600 | `-0.025em`, `tabular-nums` |
| Body / table | 12.5–13px | 400 | |
| Label / caption | 11.5–12px | 400–500 | `--v2-muted` |
| Micro / meta | 11px | 400 | `--v2-faint` |

- Radius: **8px** controls and cards, **11–12px** feature cards and popovers.
- Card padding: 16–18px. Grid gap: 10–12px.
- **Every figure gets `font-variant-numeric: tabular-nums`** so columns line up
  and a changing value does not reflow its neighbours.
- Sentence case everywhere. No `text-transform: uppercase` with letter-spacing
  — that was the v1 tell.

---

## 5. Components

### Stat tile

```
label (12px muted, with an optional 10x2px series key)
value (29px ink, tabular)
delta (12px: ↗/↘ glyph + percent + "vs 2025" in faint)
[optional 3px progress bar on a --v2-sunk track]
footer (11.5px faint — where the number came from)
```

White surface, `1px solid var(--v2-line)`, radius 11px, no shadow. Up is
`--v2-pos`, down is `--v2-warn` — **not** `--v2-neg`. A softer month is
information, not an error; a dashboard that shouts at every dip stops being
read. Reserve red for things that are actually wrong.

### Buttons

| Class | Fill | Label |
|---|---|---|
| `.btn-s` / `.btn-primary` | `--v2-ink` | `--v2-ground` |
| `.btn-p` / `.btn-o` / `.btn-secondary` / `.btn-success` | `--v2-surface` | `--v2-ink`, `--v2-line-2` border |
| `.btn-accent` | `--v2-accent` | `--v2-ground` |
| `.btn-d` | `--v2-surface` | `--v2-neg` text |
| `.btn-danger` | `--v2-neg` | `--v2-ground` |

**Label a filled button with `--v2-ground`, never `#fff`.** Both fills are
tokens that invert with the theme: `--v2-neg` is a deep red on light but a pale
salmon on dark, where white text measured 2.54:1. Riding the ground token keeps
every combination at 4.7:1 or better.

Exactly **one filled button per view** — the committing action. Everything else
is an outline.

### Tables

Hairline rows, no vertical rules, quiet headers: `--v2-muted` at 11.5px weight
500, one `--v2-line` under the header, `--v2-line` between rows, row hover
`--v2-rail`. Numbers right-aligned and tabular. No zebra striping.

### Filters

One row, above the content, right-aligned in the page header. Native `<select>`
styled as a bordered chip with a CSS-drawn caret (`appearance: none`). Submit on
`change` via **GET**, so a filtered view is a shareable URL that survives a
refresh, and an unparseable parameter falls back to the default rather than
returning 400.

### Charts

See §6.

---

## 6. Data visualisation

The house style is **emphasis, not a categorical palette**: the subject series
in ink, context series in `--v2-muted`, and the accent on the one series the
chart exists to answer.

Rules that are not negotiable:

- **Never a dual-axis chart.** Two measures of different scale → two charts, or
  index both to a common base.
- **Colour follows the entity, never its rank.** Filtering out a series must not
  repaint the survivors.
- **2px lines, ≥8px hover markers, hairline solid gridlines, area fills ~10%.**
- **Axis ticks round to 1/2/2.5/5 × a power of ten.** `_nice_step()` in
  `shared/invoicing_performance.py` does this; reuse it.
- **A legend is always present for two or more series**, carrying values as well
  as names. Direct-label selectively — the endpoint or the extreme, never every
  point.
- **Text never wears the data colour.** Identity comes from a swatch beside the
  text; labels stay in ink/muted.
- **Ship the table view.** `<details>` under the chart with every value. It is
  the chart's accessible twin, not a fallback.
- **Give every series a second identity channel** where you can — the dashed
  purchases line is what lets that chart survive greyscale printing and colour
  vision deficiency.

Geometry belongs in Python, not Jinja: a template is the one place in this
codebase nothing can unit-test. `shared/invoicing_performance.py` computes
points, ticks and the tooltip payload; the template only prints them.

**Validate any palette before shipping it.** The dataviz skill's
`scripts/validate_palette.js` computes CVD separation, normal-vision separation
and contrast — run it rather than eyeballing. Targets: CVD ΔE ≥ 8, normal-vision
ΔE ≥ 15, every mark ≥ 3:1 against its surface. The current chart measures 8.4
light / 12.7 dark, 15.3 / 17.3, and all marks clear 3:1. Note the validator's
lightness-band and chroma-floor checks describe a *categorical hue set* and will
fail an emphasis palette by construction — that is expected; the neutrals read
grey on purpose.

---

## 7. Dark mode

Dark **follows the OS, and the topbar toggle overrides it** (moon/sun, right of
the company switcher). Precedence:

| `data-theme` on `<html>` | Result |
|---|---|
| `"dark"` | dark, whatever the OS says |
| `"light"` | light, whatever the OS says |
| absent | follows `prefers-color-scheme` |

The toggle writes `data-theme` and persists it under `localStorage["ax-theme"]`;
an inline script in `<head>` re-applies it before first paint, so an explicit
choice never flashes the other theme.

This was opt-in-only for the whole rollout — the OS signal would have darkened
the shared shell around modules that had no dark palette at all — and was turned
on in the same change that put the last module into `UI_V2_MODULES`.

Two structural traps, both of which have bitten this file:

- **The two dark blocks must stay identical.** CSS cannot share one declaration
  list across a media-query boundary, so the dark palette is written twice —
  once for `[data-ui="v2"][data-theme="dark"]`, once for
  `[data-ui="v2"]:not([data-theme="light"])` inside the media query.
  `tests/unit/test_ui_css_palette.py` fails if they drift.
- **`@media` adds no specificity.** The print block's selector has to answer
  every dark selector at equal-or-higher specificity *and* come last, or dark
  wins and the page prints near-white text onto paper. That is a live bug this
  file shipped with, caught by the same test.

Writing dark-safe CSS:

- **Never hardcode a light value.** `background: #f8fafc` and
  `linear-gradient(#fbfcfe, #f1f4fa)` are near-white blocks that keep their
  colour when the text around them turns light. Every one of these was a
  real unreadable header on this codebase.
- **Tailwind's colour utilities are hardcoded light values.** `text-slate-400`
  and `text-red-600` are exactly the trap above wearing a class name. The
  finance reports carried ~78 of them; they are now `.t-muted` / `.num-neg`
  helpers that resolve to tokens.
- **A token that inverts must invert everything that sits on it.** If the fill
  is `--v2-ink`, the label is `--v2-ground` — not `#fff`.
- **On an inverted fill, the meaning tokens stop working.** `--v2-neg` on a
  `--v2-ink` slab is dark-on-dark in light mode and pale-on-pale in dark mode.
  Ride `--v2-ground` there and let the sign character carry the meaning —
  `socie.html`'s grand-total row is the worked example.
- **Print stays light.** `ui.css` re-declares the light palette inside
  `@media print`; nobody wants an invoice that prints a black rectangle.

Verify by measurement, not by eye. The audit that caught the failures above
walks each page with the theme forced, computes the rendered contrast of every
text node against its true background, and flags anything under 3:1 or sitting
on a gradient.

---

## 8. Accessibility floors

- Body text ≥ 4.5:1; large text and UI marks ≥ 3:1.
- Focus is visible: `2px solid var(--v2-ink)` at `2px` offset, never
  `outline: none`.
- Colour is never the only channel — pair it with a label, glyph or dash.
- Responsive to 320px with no horizontal page scroll; wide tables and charts
  scroll inside their own `overflow-x: auto` container.

---

## 9. Bringing a page over — checklist

1. Add the module key to `UI_V2_MODULES` — or, for a page that renders outside
   the shell, stamp `<html data-ui="v2">` and include `partials/_v2_head.html`
   (§2).
2. Delete the page's private palette; replace its `:root` with the bridge block
   (every name resolving to a v2 token, literals only as fallbacks).
3. Grep the page for hex literals inside `<style>` **and** for Tailwind colour
   utilities. Each one is a dark-mode bug waiting:
   `grep -oE '#[0-9a-fA-F]{3,8}'` and
   `grep -oE '\b(bg|text|border)-(slate|gray|red|green|blue|amber)-[0-9]{2,3}\b'`.
   `tests/unit/test_shell_palette_literals.py` runs both greps over every v2
   template so the step cannot be skipped; it also holds the shared shell to
   bridge names only, since `app_shell.html` still renders for v1 modules.
4. Replace uppercase micro-labels with sentence case; drop shadows on cards.
5. Confirm one filled button per view.
6. Run the contrast audit in both themes; fix anything under 3:1.
7. Screenshot both themes at 1400px and 375px and **look at them** — the audit
   checks colour, not layout.
8. Update the E2E tests (`AGENTS.md` §2) and run the suite.
