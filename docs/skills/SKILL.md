---
name: purchase-invoice-calculations
description: Comprehensive calculation and UI rules for building or verifying purchase invoices that have three independent Combined/Per-Item toggles (Discount, Expenses, Tax), pro-rata vs manual per-item allocation, Sales Tax, and Withholding (W/H) Tax. Use this skill whenever generating, calculating, validating, or explaining a purchase invoice, purchase order costing, or any document with subtotal → discount → expenses → tax → net payable logic, even if the user only mentions "invoice calculation," "discount %," "commission/freight/loading," "sales tax," "W/H tax," or "Net Amount column" without using the word "skill."
---

# Purchase Invoice Calculations

This skill defines the complete, unambiguous math **and** the UI behavior for a purchase invoice that has three independent mode toggles:

| Section  | Combined mode | Per Item mode |
|----------|---------------|---------------|
| Discount | One % or Value applied to the whole invoice | **Pro-rata** (auto-split) OR **Manual** (entered per line) |
| Expenses | One set of Commission / Freight / Loading-Unloading values applied to the whole invoice | **Pro-rata** (auto-split) OR **Manual** (entered per line) |
| Tax (Sales Tax — W/H Tax is always Combined) | One % applied to the whole invoice | **Manual only** — no pro-rata option |

Each of the three toggles (Discount, Expenses, Tax) is independent — any combination is valid (e.g. Discount=Combined, Expenses=Per Item/Pro-rata, Tax=Per Item/Manual all on the same invoice).

---

## 1. Data Model

**Per line item:**
- `Qty` — quantity
- `Rate` — unit rate
- `Amount = Qty × Rate` (gross line amount)
- `ItemDiscountPct` / `ItemDiscountVal` — only meaningful when Discount = Per Item / Manual
- `ItemExpenseVal` (Commission/Freight/Loading, each separate) — only meaningful when Expenses = Per Item / Manual
- `ItemSalesTaxPct` — only meaningful when Tax = Per Item (always manual)

**Invoice-level (Combined mode inputs — also used as the "pool" for Per Item/Pro-rata):**
- `DiscountPct` or `DiscountVal` (mutually exclusive — see §2.3 on the "%"/"Val" toggle)
- `Commission`, `Freight`, `LoadingUnloading` — flat Rupee values (never percentages)
- `SalesTaxPct`, `WHTaxPct`

**Derived invoice-level totals:**
- `Subtotal = Σ Amount` across all lines
- `TotalDiscount`
- `TotalExpenses = Commission + Freight + LoadingUnloading`
- `TaxBase` — the amount Sales Tax and W/H Tax are both calculated on
- `SalesTax`, `WHTax`
- `NetPayable`

**Derived per-line value (display only, see §6 for when it's shown):**
- `NetAmount_i = Amount_i − ItemDiscount_i + ItemExpense_i` — the line's value after its own discount and expense share, feeding that line's tax base.

---

## 2. Step-by-Step Algorithm

### Step 1 — Line Amounts
For every line: `Amount = Qty × Rate`. `Subtotal = Σ Amount`.

### Step 2 — Discount

**2.1 Combined mode**
```
TotalDiscount = DiscountPct% × Subtotal      (if entered as %)
   or
TotalDiscount = DiscountVal                  (if entered as a flat Value)
```
Line items are NOT individually discounted; each line's net amount for downstream steps is its share of `Subtotal − TotalDiscount` only in aggregate (the invoice does not need to track per-line net amount at all — everything downstream in Combined mode works off invoice-level totals).

**2.2 Per Item — Pro-rata**
An overall discount pool (`DiscountPct%` or a flat `DiscountVal`) is entered once, then automatically split across lines by each line's share of the subtotal:
```
LineWeight_i   = Amount_i / Subtotal
ItemDiscount_i = TotalDiscount × LineWeight_i
NetAmount_i    = Amount_i − ItemDiscount_i
```
`TotalDiscount = Σ ItemDiscount_i` (should reconcile to the entered pool up to rounding — see §4 Rounding).

**2.3 Per Item — Manual**
Each line has its own `ItemDiscountPct` (or `ItemDiscountVal`):
```
ItemDiscount_i = ItemDiscountPct_i% × Amount_i     (or the flat ItemDiscountVal_i)
NetAmount_i    = Amount_i − ItemDiscount_i
TotalDiscount  = Σ ItemDiscount_i
```
There is no invoice-level `DiscountPct` input in this sub-mode — the total is purely a rollup of the lines, and can differ from what a single uniform % would produce. This is expected and correct.

**% vs Val toggle:** Discount (in both Combined and Per Item/Manual) can be entered either as a percentage of the relevant amount or as a direct flat Rupee value. Only one of the two is active at a time per line/invoice — never sum both.

### Step 3 — Expenses (Commission, Freight, Loading/Unloading)

Expenses are always entered as flat Rupee amounts (never %), whether Combined or Per Item.

**3.1 Combined mode**
```
TotalExpenses = Commission + Freight + LoadingUnloading
```
Applied once at the invoice level; not attributed to specific lines.

**3.2 Per Item — Pro-rata**
Enter the three combined totals once (`Commission`, `Freight`, `LoadingUnloading`), then auto-split each across lines by weight. Use the **post-discount net amount** as the allocation basis (not the gross amount), since that reflects each line's true remaining value:
```
LineWeight_i     = NetAmount_i / Σ NetAmount
ItemExpense_i    = TotalExpenses × LineWeight_i
```
(If Discount = Combined while Expenses = Per Item/Pro-rata, use `Amount_i / Subtotal` as the weight instead, since no per-line net amount exists yet.)

**3.3 Per Item — Manual**
Each line has its own `ItemCommission_i`, `ItemFreight_i`, `ItemLoadingUnloading_i` entered directly:
```
ItemExpense_i = ItemCommission_i + ItemFreight_i + ItemLoadingUnloading_i
TotalExpenses = Σ ItemExpense_i
```

### Step 4 — Tax Base

The **same base** feeds both Sales Tax and W/H Tax — they are calculated independently off it, never compounded on each other.

**Combined mode (Discount & Expenses also Combined):**
```
TaxBase = Subtotal − TotalDiscount + TotalExpenses
```

**Per Item mode (Tax = Per Item, always manual):**
Each line has its own tax base:
```
LineTaxBase_i = NetAmount_i + ItemExpense_i
```
(`NetAmount_i` and `ItemExpense_i` come from whichever sub-mode Steps 2–3 used. If Discount and/or Expenses are still Combined while Tax is Per Item, first derive a notional per-line net/expense by pro-rating those combined totals by `Amount_i / Subtotal` purely for the purpose of building each line's tax base — the invoice-level Discount/Expenses totals themselves stay Combined and unsplit for display.)

### Step 5 — Sales Tax & W/H Tax

**Combined mode:**
```
SalesTax = SalesTaxPct%  × TaxBase
WHTax     = WHTaxPct%     × TaxBase
```

**Per Item mode (manual only — no pro-rata option for tax):**
```
ItemSalesTax_i = ItemSalesTaxPct_i% × LineTaxBase_i
SalesTax = Σ ItemSalesTax_i
```
W/H Tax always uses Combined mode — it is calculated off the invoice-level `TaxBase`, never per item.
Tax is manual-only per item because tax rates legitimately differ by item (different HS codes, exempt items, zero-rated items) — unlike Discount/Expenses, there's no sensible "auto-split" for a rate that varies by product classification.

### Step 6 — Net Payable

Sales Tax is added, W/H Tax is subtracted (W/H tax is withheld from the payment, not paid to the vendor):
```
NetPayable = TaxBase + SalesTax − WHTax
           = (Subtotal − TotalDiscount + TotalExpenses) + SalesTax − WHTax
```
This identity holds regardless of which sections are Combined vs Per Item, as long as `TotalDiscount`, `TotalExpenses`, `SalesTax`, and `WHTax` are each computed as the sum of whatever mode was used for that section.

---

## 3. Rounding Rules

1. Round every displayed line-level and invoice-level monetary value to 2 decimal places.
2. When pro-rating (Discount or Expenses, §2.2/§3.2), rounding line-by-line can leave the sum of the rounded lines off by a paisa/cent from the entered combined pool. Resolve by adjusting the **last line** by the residual (`pool − Σ(all-but-last rounded lines)`) so the displayed total always reconciles exactly to the entered pool.
3. Do not round intermediate weights (`LineWeight_i`) — only round the final monetary output of each step.
4. `TaxBase` should be computed from already-rounded `Subtotal`, `TotalDiscount`, and `TotalExpenses` (i.e. round before computing tax, not after), so Sales Tax/W/H Tax match what a human checking the printed invoice would recompute by hand.

---

## 4. Worked Examples

### Example A — Fully Combined (matches reference screenshot)
Single item, Qty 1 × Rate 55,000 → Amount = Subtotal = 55,000.00
- Discount 18% → `TotalDiscount = 9,900.00`
- Expenses: Commission 5,000 + Freight 10,000 + Loading/Unld 5,000 → `TotalExpenses = 20,000.00`
- `TaxBase = 55,000 − 9,900 + 20,000 = 65,100.00`
- Sales Tax 18% → `11,718.00`
- W/H Tax 3% → `1,953.00`
- `NetPayable = 65,100 + 11,718 − 1,953 = 74,865.00`

### Example B — Mixed modes (Discount=Per Item/Manual, Expenses=Per Item/Pro-rata, Tax=Per Item/Manual)
Two items:
| Item | Qty×Rate | Amount |
|---|---|---|
| A | 2 × 1,000 | 2,000.00 |
| B | 1 × 3,000 | 3,000.00 |

Subtotal = 5,000.00

**Discount (manual per line):** A = 10% → 200.00; B = 5% → 150.00
- `NetAmount_A = 1,800.00`, `NetAmount_B = 2,850.00`
- `TotalDiscount = 350.00`

**Expenses (pro-rata, base = net amount, combined pool = 500.00):**
- Weight_A = 1,800/4,650 = 38.71% → Expense_A = 193.55
- Weight_B = 2,850/4,650 = 61.29% → Expense_B = 306.45 (adjusted to balance to 500.00 exactly per §3.2)
- `TotalExpenses = 500.00`

**Line tax bases:**
- `LineTaxBase_A = 1,800.00 + 193.55 = 1,993.55`
- `LineTaxBase_B = 2,850.00 + 306.45 = 3,156.45`

**Tax (manual per line):** A → Sales Tax 18%; B → Sales Tax 17%
- `ItemSalesTax_A = 358.84`
- `ItemSalesTax_B = 536.60`
- `SalesTax = 895.44`

**W/H Tax (always Combined):** Applied on invoice-level TaxBase at 3%
- `WHTax = 5,150.00 × 3% = 154.50`

**Net Payable:**
```
TaxBase total = 1,993.55 + 3,156.45 = 5,150.00
NetPayable = 5,150.00 + 895.44 − 154.50 = 5,890.94
```

---

## 5. Edge Cases & Validation Rules

- **Negative discount (markup/surcharge):** Treat as a normal signed value — `TotalDiscount` can be negative, which increases `TaxBase`. Formulas are unchanged.
- **Zero-rated / tax-exempt line items:** Only reachable when Tax = Per Item/Manual; set that line's `ItemSalesTaxPct` to 0.
- **Zero quantity or zero rate lines:** `Amount = 0`; contributes 0 weight in any pro-rata split (guard against divide-by-zero when `Subtotal` or `Σ NetAmount = 0` — if the divisor is 0, skip pro-rata and set all allocations to 0).
- **Discount entered as Val vs %:** Never apply both simultaneously to the same scope (invoice or line). Whichever was last edited by the user is authoritative; the other field should just reflect the equivalent computed value for display.
- **Switching modes mid-edit:** If a user flips Discount/Expenses from Combined → Per Item/Pro-rata, seed the pro-rata split from the last known combined pool. If flipping Per Item/Manual → Combined, the combined field should default to the *rollup total* of the manual lines (as a flat Value), not silently reset to 0 or a stale %.
- **W/H Tax never compounds on Sales Tax:** Both are always calculated off the same `TaxBase` (or `LineTaxBase_i`), independently of one another — never `WHTaxPct% × (TaxBase + SalesTax)`.
- **Commission/Freight/Loading are never percentages:** Even in Per Item/Manual mode, these three expense fields are flat Rupee entries per line, not rates.
- **Rounding reconciliation:** Per §3.2/§4.2 of Rounding Rules, always force the visible line-level sum to equal the entered/derived combined total exactly — never let the printed invoice show a total that doesn't match the sum of its own visible line items.

---

## 6. UI Interaction Spec

This section is for whoever implements the invoice screen (form + table + summary). It translates §1–5 into concrete on-screen behavior so an implementer doesn't have to infer it.

### 6.1 The three toggles
Discount, Expenses, and Tax each get their own two-state control: **Combined | Per Item**. They are independent — never gate one toggle's availability on another's state.

When Discount or Expenses is switched to **Per Item**, reveal a secondary two-state control beneath it: **Auto-split (pro-rata) | Manual**. Default a freshly-toggled Per Item section to Auto-split, since it requires no extra input from the user and reuses whatever combined pool was already entered (see the mode-switch rule in §5). Tax has no secondary control — Per Item tax is always manual, per §Step 5.

### 6.2 Column visibility in the line-items table
Columns appear or disappear based on the active sub-mode, not just the top-level toggle:

| Section state | Extra column(s) shown | Editable? |
|---|---|---|
| Discount = Per Item / Manual | `Disc %`, `Disc Val` | Yes, per line |
| Discount = Per Item / Auto-split | `Discount` (computed) | No — read-only, shows each line's split |
| Discount = Combined | *(none)* | — |
| Expenses = Per Item / Manual | `Comm.`, `Freight`, `Load/Unld` | Yes, per line |
| Expenses = Per Item / Auto-split | `Expenses` (computed) | No — read-only |
| Expenses = Combined | *(none)* | — |
| Tax = Per Item | `Tax %` | Yes, per line |
| Tax = Combined | *(none)* | — |

Never show an editable per-line input for a section that's Combined — those values live only in the summary panel below the table while Combined is active.

### 6.3 The `Net Amount` column
`Net Amount` is a **derived, read-only** column: `Amount − ItemDiscount + ItemExpense` for that line (§1). It exists to show what a line is actually worth after its own discount/expense allocation, and it's what that line's tax gets calculated on when Tax = Per Item.

- **Show it** whenever Discount and/or Expenses is Per Item (either sub-mode) — there's a real per-line adjustment to display.
- **Hide it** when Discount and Expenses are both Combined. In that state no line carries an individual net value (§2.1) — `Net Amount` would just equal `Amount` on every row, which is redundant. Don't render a column that never differs from `Amount`; it invites the user to think a per-line discount was applied when it wasn't.

### 6.4 The summary panel (below the entry form)
The summary panel is the home for every **Combined**-mode value, plus the pool inputs for **Per Item/Auto-split** (since pro-rata still starts from a single entered pool — see §2.2/§3.2). It always shows, top to bottom: `Subtotal`, `Discount`, `Commission`, `Freight`, `Loading/Unld`, `Total Expenses`, `Sales Tax`, `W/H Tax`, `Net Payable`.

- **Combined or Per Item/Auto-split:** the corresponding field(s) in the summary stay editable — this is where the user sets the % / flat Value or the flat Rupee pool.
- **Per Item/Manual:** the corresponding summary field becomes read-only, displaying the rollup total computed from the line items (per the mode-switch rule in §5 — never blank or zero it out).
- `Sales Tax` in the summary is editable only when Tax = Combined; in Per Item mode it's a read-only rollup of the per-line tax.
- `W/H Tax` is always editable in the summary — it never has a per-item counterpart.
- `Total Expenses` and `Net Payable` are always read-only rollups, regardless of mode.

### 6.5 General rules
- Re-derive the full calculation (§2–§6 of the math spec) on every edit — qty/rate change, mode toggle, or any input blur — rather than patching individual totals, to avoid drift between what's displayed and what the formulas would produce.
- Apply the rounding/residual rule (§3) after every recompute, not just on save, so on-screen totals always match what a user could verify by hand-adding the visible line items.

---

## 7. Quick Reference

```
Subtotal      = Σ Amount_i                 (Amount_i = Qty_i × Rate_i)
TotalDiscount = combined(% or Val) | Σ pro-rata split | Σ per-line manual
TotalExpenses = combined(Commission+Freight+Loading) | Σ pro-rata split | Σ per-line manual
TaxBase       = Subtotal − TotalDiscount + TotalExpenses      (or per-line: NetAmount_i + ItemExpense_i)
SalesTax      = combined %×TaxBase | Σ per-line %×LineTaxBase_i   (Tax is NEVER pro-rata)
WHTax         = WHTaxPct% × TaxBase   (always Combined, never per-item)
NetPayable    = TaxBase + SalesTax − WHTax
NetAmount_i   = Amount_i − ItemDiscount_i + ItemExpense_i     (display-only; shown only when Discount and/or Expenses is Per Item)
```
