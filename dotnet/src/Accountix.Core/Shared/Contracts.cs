using Accountix.Core.Entities;

namespace Accountix.Core.Shared;

// Services one module provides and others call. Each has ONE owning module
// (named on the interface), which implements it and registers it in its
// IModule; until then a stub throwing NotYetPortedException stands in.
// Signatures mirror the Python functions named in each summary — keep the
// behaviour identical (same journals, same numbers, same refusals), since
// both apps share the database while the move is in progress.
//
// Owners may ADD members (build Accountix.Core right after); never change or
// remove one another module may already call.

/// <summary>An operation whose module has not been moved to .NET yet.</summary>
public sealed class NotYetPortedException(string what) : NotImplementedException($"{what} has not been moved to .NET yet.");

/// <summary>A refusal the user should read (closed period, bad date, negative stock...). Answered 409 / flash, never 500.</summary>
public class BusinessRuleException(string message) : InvalidOperationException(message);

public sealed class ClosedPeriodException(string message) : BusinessRuleException(message);
public sealed class DocumentDateException(string message) : BusinessRuleException(message);
public sealed class NegativeStockException(string message) : BusinessRuleException(message);
public sealed class ConsumedLayerException(string message) : BusinessRuleException(message);

/// <summary>One journal line to post (shared/ledger_utils.post_journal_entry's line dict).</summary>
public sealed record JournalLineInput(int AccountId, decimal Debit, decimal Credit, string? Description = null, int? LabelId = null);

/// <summary>
/// General-ledger posting. Owner: Accounting. Python: shared/ledger_utils.py,
/// shared/gl_summary.py (gl_daily_balances is maintained in the same
/// transaction as every posting/un-posting).
/// </summary>
public interface ILedger
{
    /// <summary>post_journal_entry: balanced (±0.01), postable accounts only, period open, then gl_daily_balances.</summary>
    Task<JournalEntry> PostAsync(string voucherType, int voucherId, string voucherNumber, string description,
                                 IReadOnlyList<JournalLineInput> lines, DateTime? entryDate = null,
                                 int createdBy = 1, CancellationToken ct = default);

    /// <summary>reverse_journal_entry: un-post the voucher's active entries (used on unapprove/delete).</summary>
    Task ReverseAsync(string voucherType, int voucherId, int createdBy = 1, CancellationToken ct = default);

    /// <summary>posting_account(role): the account for a semantic role ("inventory", "cogs", "sales_tax_payable"...), created if missing.</summary>
    Task<ChartOfAccount> PostingAccountAsync(string role, CancellationToken ct = default);

    /// <summary>create_entity_account(kind, entity_id, name): level-5 subledger for a customer/supplier/employee/...</summary>
    Task<ChartOfAccount> EntityAccountAsync(string kind, int entityId, string name, CancellationToken ct = default);

    /// <summary>party_account(kind, entity_id, name, override_account_id).</summary>
    Task<ChartOfAccount> PartyAccountAsync(string kind, int entityId, string name, int? overrideAccountId = null, CancellationToken ct = default);

    /// <summary>get_or_create_account(code, name, type, parent_code).</summary>
    Task<ChartOfAccount> GetOrCreateAccountAsync(string code, string name, string type, string? parentCode = null, CancellationToken ct = default);

    /// <summary>cash_bank_accounts(): posting accounts money moves through.</summary>
    Task<IReadOnlyList<ChartOfAccount>> CashBankAccountsAsync(CancellationToken ct = default);

    /// <summary>entity_has_postings(kind, entity_id).</summary>
    Task<bool> EntityHasPostingsAsync(string kind, int entityId, CancellationToken ct = default);
}

/// <summary>Voucher number series (VoucherNumber): atomic, gapless, numbered in commit order. Owner: Accounting.</summary>
public interface IVoucherNumbers
{
    Task<string> NextAsync(string prefix, CancellationToken ct = default);
}

/// <summary>Financial periods (shared/periods.py). Owner: Accounting.</summary>
public interface IPeriods
{
    /// <summary>require_open_period: FOR SHARE on the period; ClosedPeriodException when closed. A date no period covers is allowed.</summary>
    Task<AccountingPeriod?> RequireOpenAsync(DateTime when, string action = "post to", CancellationToken ct = default);
    Task<AccountingPeriod?> PeriodForAsync(DateTime when, CancellationToken ct = default);
}

/// <summary>
/// Stock costing (shared/costing.py, cost_adjustment.py, stock_entry.py):
/// FIFO / weighted-average layers in document-date order, replay of later
/// issues, CADJ journals for re-costed issues. Owner: Inventory.
/// </summary>
public interface IStockCosting
{
    /// <summary>lock_products: FOR NO KEY UPDATE on the products, ascending ids.</summary>
    Task LockProductsAsync(IEnumerable<int> productIds, CancellationToken ct = default);

    /// <summary>record_in. Returns the stock_ledger row's total cost.</summary>
    Task<decimal> RecordInAsync(int productId, string voucherType, int voucherId, string voucherNumber,
                                decimal qty, decimal unitCost, string notes = "", int createdBy = 1,
                                DateTime? txnDate = null, decimal? totalCost = null, CancellationToken ct = default);

    /// <summary>record_out. Returns the cost of the issue. <paramref name="preferVoucher"/> = the "prefer" (type, id) for purchase returns.</summary>
    Task<decimal> RecordOutAsync(int productId, string voucherType, int voucherId, string voucherNumber,
                                 decimal qty, string notes = "", int createdBy = 1, decimal? unitCost = null,
                                 DateTime? txnDate = null, (string Type, int Id)? preferVoucher = null,
                                 CancellationToken ct = default);

    /// <summary>reverse_voucher_stock: remove a voucher's rows, give back what they consumed, re-cost later issues.</summary>
    Task ReverseVoucherStockAsync(string voucherType, int voucherId, int createdBy = 1, CancellationToken ct = default);

    Task<decimal> OnHandAsync(int productId, DateTime? at = null, CancellationToken ct = default);
    Task<decimal> StockValueAsync(int productId, CancellationToken ct = default);
    Task<decimal> CurrentUnitCostAsync(int productId, CancellationToken ct = default);

    /// <summary>original_issue_cost(voucher_type, voucher_id, product_id): what an issue cost (returns follow it).</summary>
    Task<decimal?> OriginalIssueCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default);

    // ── Added by Inventory (29 Oct): the rest of shared/costing.py that callers use ──

    /// <summary>record_in, returning the stock_ledger row (its unit_cost / total_cost are what to post).</summary>
    Task<StockLedger> ReceiveAsync(int productId, string voucherType, int voucherId, string voucherNumber,
                                   decimal qty, decimal unitCost, string notes = "", int createdBy = 1,
                                   DateTime? txnDate = null, decimal? totalCost = null, CancellationToken ct = default);

    /// <summary>record_out, returning (unit_cost, total_cost): the cost to post. NegativeStockException when not on hand.</summary>
    Task<(decimal UnitCost, decimal TotalCost)> IssueAsync(int productId, string voucherType, int voucherId, string voucherNumber,
                                                          decimal qty, string notes = "", int createdBy = 1, decimal? unitCost = null,
                                                          DateTime? txnDate = null, (string Type, int Id)? preferVoucher = null,
                                                          CancellationToken ct = default);

    /// <summary>cost_of_issue: (unit, total) issuing qty would carry now. Pure.</summary>
    Task<(decimal UnitCost, decimal TotalCost)> CostOfIssueAsync(int productId, decimal qty, CancellationToken ct = default);

    /// <summary>value_at: stock value at the end of that day (document-date view).</summary>
    Task<decimal> ValueAtAsync(int productId, DateTime at, CancellationToken ct = default);

    /// <summary>original_receipt_cost: the (quantity-weighted) unit cost a voucher received the product at; purchase returns leave at it.</summary>
    Task<decimal?> OriginalReceiptCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default);

    /// <summary>consumers_of_voucher: issues that drew on this voucher's layers, as (type, number, qty).</summary>
    Task<IReadOnlyList<(string VoucherType, string VoucherNumber, decimal Qty)>> ConsumersOfVoucherAsync(string voucherType, int voucherId, CancellationToken ct = default);

    /// <summary>reverse_voucher_stock(allow_variance=...): returns {product_id: moved cost}. allowVariance = correcting an earlier receipt (PI unapprove).</summary>
    Task<IReadOnlyDictionary<int, decimal>> ReverseVoucherStockAsync(string voucherType, int voucherId, bool allowVariance, int createdBy = 1,
                                                                    CancellationToken ct = default);

    /// <summary>revalue_for_method_change: call when Inventory Settings switches valuation method (before saving the new method).</summary>
    Task RevalueForMethodChangeAsync(string newMethod, int createdBy = 1, CancellationToken ct = default);

    /// <summary>assert_invariant: layers' carried value equals the ledger's running cost (±0.01).</summary>
    Task<(bool Ok, decimal LayerValue, decimal RunningCost)> AssertInvariantAsync(int productId, CancellationToken ct = default);
}

/// <summary>Report files (shared/report_export.py): one pipeline for every Excel / PDF / CSV. Owner: Finance.</summary>
public interface IReportExport
{
    /// <summary>export_table(fmt, ...): "excel" | "pdf" | "csv" (Python's fmt values).</summary>
    Task<ExportFile> ExportTableAsync(string format, ReportTable table, CancellationToken ct = default);
}

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

public sealed class ReportTable
{
    public required string Title { get; init; }
    public string? Period { get; init; }
    /// <summary>Filter lines under the heading; Label "" prints the Value alone (Python's plain string filters).</summary>
    public IReadOnlyList<(string Label, string Value)> Filters { get; init; } = [];
    public required IReadOnlyList<ReportColumn> Columns { get; init; }
    public required IReadOnlyList<ReportRow> Rows { get; init; }
    public bool Landscape { get; init; }
    /// <summary>A column printed in a fixed-width font (codes, numbers): Python's mono_col.</summary>
    public int? MonoColumn { get; init; }
    /// <summary>The period part of the file name (file_period); null uses Period.</summary>
    public string? FilePeriod { get; init; }
    /// <summary>A column whose "account" rows are indented in the PDF (indent_col).</summary>
    public int? IndentColumn { get; init; }
    /// <summary>Rows (0-based) printed bold (bold_rows).</summary>
    public IReadOnlySet<int> BoldRows { get; init; } = new HashSet<int>();
    /// <summary>Excel column widths (characters); null: sized to the content.</summary>
    public IReadOnlyList<double>? ColumnWidths { get; init; }
    /// <summary>Excel AutoFilter; null: on for flat lists (no section rows).</summary>
    public bool? Autofilter { get; init; }
    /// <summary>The first sheet's name; null: the title.</summary>
    public string? SheetTitle { get; init; }
    /// <summary>Further plain list sheets appended to the workbook: (name, headers, rows).</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows)> ExtraSheets { get; init; } = [];
}

public sealed record ReportColumn(string Header, ReportColumnKind Kind = ReportColumnKind.Text, double? Width = null);

public enum ReportColumnKind { Text, Money, Quantity, Date, Integer, Percent }

/// <summary>A row; Style = null/"plain" | "account" | "section" | "total" | "subtotal" | "grand" | "spacer".</summary>
public sealed record ReportRow(IReadOnlyList<object?> Cells, string? Style = null);

/// <summary>
/// The audit trail (shared/audit.py). Tracked create / update / delete of
/// business records is written AUTOMATICALLY by the Platform module's EF
/// interceptor (as Python's session hook) — module code does nothing for
/// those. Call RecordNowAsync only where Python called record_now(): events
/// that must survive a rollback (refused postings, sign-ins) or touch no row.
/// Owner: Platform.
/// </summary>
public interface IAuditTrail
{
    Task RecordNowAsync(string action, string summary, string module = "admin", string? entityType = null,
                        int? entityId = null, string? reference = null, int? companyId = null,
                        object? changes = null, CancellationToken ct = default);
}

/// <summary>
/// Invoice tracking caches (shared/payment_tracking.py). Owner: Parties.
/// Accounting calls these after a voucher changes; Python treats a failure
/// here as non-fatal (a voucher save must not fail over a cache).
/// </summary>
public interface IPaymentTracking
{
    /// <summary>reconcile_voucher: refresh the invoices this voucher's assignments touch.</summary>
    Task ReconcileVoucherAsync(int voucherId, CancellationToken ct = default);

    /// <summary>Remove the assignments of a voucher about to be deleted, then refresh their invoices.</summary>
    Task DropForVoucherAsync(int voucherId, CancellationToken ct = default);

    /// <summary>
    /// sync_invoice(doc_type, doc_id): re-derive one invoice's paid_amount / payment_status after the invoice
    /// itself (or a return against it) changed. docType "SI" | "PI". Saves; the caller commits its transaction.
    /// </summary>
    Task SyncInvoiceAsync(string docType, int docId, CancellationToken ct = default);

    /// <summary>drop_allocations_for_doc(doc_type, doc_id): remove assignments pointing at an invoice about to be deleted.</summary>
    Task<int> DropForDocAsync(string docType, int docId, CancellationToken ct = default);
}

/// <summary>A submitted field that cannot be read (shared/forms.FormInputError): flashed, back to the form.</summary>
public sealed class FormInputException(string message) : BusinessRuleException(message);

/// <summary>An export format other than excel / xlsx / pdf / csv: answered 404, as Python's abort(404).</summary>
public sealed class UnknownExportFormatException(string format) : InvalidOperationException($"Unknown export format '{format}'.");
