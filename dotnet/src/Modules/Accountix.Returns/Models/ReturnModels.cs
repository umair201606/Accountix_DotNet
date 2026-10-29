namespace Accountix.Returns.Models;

public enum ReturnSide { Sales, Purchase }

/// <summary>What sales_return/form_return.html and purchase_return/form_return.html read.</summary>
public sealed class ReturnFormModel
{
    public sealed record InvoiceOption(int Id, string Number, string? PartyName, DateTime? Date);
    public sealed record SavedItem(int? ProductId, decimal? CurrentReturnQty);

    public ReturnSide Side { get; init; }
    public int? Id { get; set; }
    /// <summary>return_doc.status if return_doc else 'new'.</summary>
    public string Status { get; set; } = "new";
    public string? ReturnNumber { get; set; }
    public int? OriginalInvoiceId { get; set; }
    public DateTime? ReturnDate { get; set; }
    public string? Notes { get; set; }
    /// <summary>reverse_charges (sales) / reverse_expenses (purchase) of a saved return.</summary>
    public bool Reverse { get; set; }
    public int EditVersion { get; set; }
    public string? ApproverName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public IReadOnlyList<SavedItem> Items { get; set; } = [];
    public IReadOnlyList<InvoiceOption> Invoices { get; set; } = [];

    public bool IsSales => Side == ReturnSide.Sales;
    public bool IsNew => Status == "new";
    public bool IsUnapproved => Status == "unapproved";
    public bool IsApproved => Status == "approved";
    public string PageMode => IsApproved ? "Approved" : IsUnapproved ? "Unapproved" : "New";
    public string Base => IsSales ? "/invoicing/sales-return" : "/inventory/purchase-return";

    /// <summary>
    /// The form's "auto-load if editing" block as the Jinja loop renders it: load the
    /// invoice, then put each saved line's quantity back on its product's row.
    /// </summary>
    public string InitScript()
    {
        if (Id is null || OriginalInvoiceId is null or 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("var sel = document.getElementById('originalInvoiceSelect');\n");
        sb.Append("  if (sel.value) {\n");
        sb.Append("    await loadInvoiceData(sel.value);\n");
        foreach (var item in Items)
        {
            sb.Append("    var rows = document.querySelectorAll('#itemsBody tr');\n");
            sb.Append("    for (var i = 0; i < rows.length; i++) {\n");
            sb.Append("      if (rows[i].dataset.productId == '").Append(PyStr(item.ProductId)).Append("') {\n");
            sb.Append("        var inp = rows[i].querySelector('[data-col=\"return_qty\"]');\n");
            sb.Append("        if (inp) { inp.value = ").Append(PyFloat(item.CurrentReturnQty)).Append("; }\n");
            sb.Append("        break;\n");
            sb.Append("      }\n");
            sb.Append("    }\n");
        }
        sb.Append("    recalc();\n");
        sb.Append("  }");
        return sb.ToString();
    }

    /// <summary>{{ x }} of an id: the number, or "None".</summary>
    public static string PyStr(int? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None";

    /// <summary>{{ x }} of a Money column (a float in Python): repr, or "None".</summary>
    public static string PyFloat(decimal? v) => v is decimal d ? Accountix.Invoicing.Services.Py.Repr(Accountix.Invoicing.Services.Py.F(d)) : "None";
}

/// <summary>What list_return.html reads.</summary>
public sealed class ReturnListModel
{
    public sealed record Row(int Id, string ReturnNumber, string? InvoiceNumber, string? PartyName, DateTime? CreatedAt,
                             decimal? GrossReturnValue, decimal? NetReturnAmount, decimal? TotalCostReturned, string? Status);

    public ReturnSide Side { get; init; }
    public IReadOnlyList<Row> Rows { get; init; } = [];
    public bool IsSales => Side == ReturnSide.Sales;
    public string Base => IsSales ? "/invoicing/sales-return" : "/inventory/purchase-return";
}
