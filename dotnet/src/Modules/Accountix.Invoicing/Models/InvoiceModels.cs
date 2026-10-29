namespace Accountix.Invoicing.Models;

/// <summary>The sales invoice editor (invoices/form_inv.html).</summary>
public sealed class SalesInvoiceFormModel
{
    /// <summary>The saved invoice (Customer, SalesOrder, ApprovedByNavigation loaded), or null for a new one.</summary>
    public InvInvoice? Invoice { get; init; }
    /// <summary>invoice_items, already serialised as Python's |tojson writes it (htmlsafe), or null when empty.</summary>
    public string? InvoiceItemsJson { get; init; }
    /// <summary>invoice_charges as |tojson, or null when empty.</summary>
    public string? InvoiceChargesJson { get; init; }
    public bool PerLineLabeling { get; init; }
    public bool OrderSourced { get; init; }
    public string PartyMode { get; init; } = "relevant";
    public string? RenderedTemplate { get; init; }
    public IReadOnlyList<ProjectLabel> ProjectLabels { get; init; } = [];
    public ProjectLabel? DefaultLabel { get; init; }
    public bool ShowSourceGate { get; init; }
    /// <summary>current_user.is_admin() or current_user.is_manager().</summary>
    public bool CanApprove { get; init; }

    public string VStatus => Invoice?.VoucherStatus ?? "unapproved";
    public string PStatus => Invoice?.PaymentStatus ?? "unpaid";
    public bool IsDraft => VStatus == "unapproved";
    public bool IsApproved => VStatus == "approved";
    public bool Editable => !IsApproved;
}

/// <summary>The purchase invoice editor (purchase_invoice/form_inv.html).</summary>
public sealed class PurchaseInvoiceFormModel
{
    /// <summary>The saved invoice (Supplier, ApprovedByNavigation loaded), or null for a new one.</summary>
    public InvPurchaseInvoice? Invoice { get; init; }
    public string? InvoiceItemsJson { get; init; }
    public string? InvoiceChargesJson { get; init; }
    public bool PerLineLabeling { get; init; }
    public string PartyMode { get; init; } = "relevant";
    public string? RenderedTemplate { get; init; }
    public IReadOnlyList<ProjectLabel> ProjectLabels { get; init; } = [];
    public ProjectLabel? DefaultLabel { get; init; }
    public bool ShowSourceGate { get; init; }
    public bool CanApprove { get; init; }

    /// <summary>The template's `status`: invoice.status, else 'new' (Python: a None status prints "None").</summary>
    public string Status => Invoice is null ? "new" : Invoice.Status ?? "None";
    public bool IsNew => Status == "new";
    public bool IsUnapproved => Status == "unapproved";
    public bool IsApproved => Status == "approved";
    public bool Editable => !IsApproved;
}

/// <summary>A row of the sales invoice list.</summary>
public sealed record SalesInvoiceRow(int Id, string InvoiceNumber, string? CustomerName, DateTime? CreatedAt, DateTime? DueDate,
    decimal? Subtotal, decimal? TotalTax, decimal? TotalFurtherTax, decimal? TotalWithholdingTax, decimal? TotalAmount,
    decimal? PaidAmount, string? VoucherStatus, string? PaymentStatus, DateTime? ApprovedAt);

/// <summary>A row of the purchase invoice list.</summary>
public sealed record PurchaseInvoiceRow(int Id, string InvoiceNumber, string VoucherNumber, string? SupplierName, DateTime? CreatedAt,
    decimal? Subtotal, decimal? TotalTax, decimal? TotalFurtherTax, decimal? TotalWithholdingTax, decimal? TotalAmount,
    decimal? PaidAmount, string? Status, string? PaymentStatus, DateTime? ApprovedAt);

/// <summary>Page links (tracking/_pager.html): 100 rows a page, every other query argument kept.</summary>
public sealed record Pager(int Page, int Pages, int Total, int First, int Last)
{
    public const int Size = 100;

    public static Pager For(int total, int page)
    {
        int pages = Math.Max(1, (total + Size - 1) / Size);
        page = Math.Clamp(page, 1, pages);
        int first = total == 0 ? 0 : (page - 1) * Size + 1;
        int last = Math.Min(total, page * Size);
        return new Pager(page, pages, total, first, last);
    }

    public int Skip => (Page - 1) * Size;
}

public sealed class InvoiceListModel<TRow>
{
    public IReadOnlyList<TRow> Rows { get; init; } = [];
    public Pager Pager { get; init; } = Pager.For(0, 1);
    public string Status { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}

/// <summary>The invoicing dashboard (dashboard/index_invoicing.html).</summary>
public sealed class DashboardModel
{
    public required Services.Performance Perf { get; init; }
    public required Services.ChartGeometry Chart { get; init; }
    public required string ChartJson { get; init; }
    public IReadOnlyList<int> Years { get; init; } = [];
    public IReadOnlyList<ProjectLabel> Labels { get; init; } = [];
    public int SelYear { get; init; }
    public int? SelLabel { get; init; }
}
