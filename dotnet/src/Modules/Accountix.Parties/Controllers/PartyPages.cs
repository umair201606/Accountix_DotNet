using Accountix.Parties.Services;
using ClosedXML.Excel;

namespace Accountix.Parties.Controllers;

/// <summary>A customer or supplier row of the list page.</summary>
public sealed record PartyListRow(int Id, string Name, string? ContactPerson, string? Phone, string? Email, string? City,
                                  decimal? CreditLimit, bool IsActive, int Orders);

public sealed class PartyListModel
{
    public required PartyKind Kind { get; init; }
    public IReadOnlyList<PartyListRow> Rows { get; init; } = [];
    public string Q { get; init; } = "";
}

/// <summary>The form: the saved record and / or what was typed (draft) when it was refused.</summary>
public sealed class PartyFormModel
{
    public required PartyKind Kind { get; init; }
    public int? Id { get; init; }
    /// <summary>Field values to show (the draft when refused, else the record), null on a blank new form.</summary>
    public Dictionary<string, string>? Fields { get; init; }
    public bool IsActive { get; init; }
    public bool IsEdit => Id is not null;
}

public sealed class BatchModel
{
    public required PartyKind Kind { get; init; }
    public IReadOnlyList<PartyValues> Rows { get; init; } = [];
    public int Created { get; init; }
    public int Flagged => Rows.Count(r => r.RowErrors.Count > 0);
}

/// <summary>
/// invoicing_app/routes/customers.py and suppliers.py, written once: the two blueprints differ only in names,
/// the credit limit and the delete guard. The controllers carry the routes and rights; this does the work.
/// </summary>
public sealed class PartyPages(AccountixDbContext db, ILedger ledger, CompanyClock clock)
{
    private const string FormView = "~/Areas/Parties/Views/Parties/Form.cshtml";
    private const string BatchView = "~/Areas/Parties/Views/Parties/Batch.cshtml";

    public async Task<IActionResult> ListAsync(Controller c, PartyKind kind, string? q, CancellationToken ct)
    {
        q ??= "";
        string like = $"%{q}%";
        List<PartyListRow> rows;
        if (kind.CreditLimit)
        {
            var query = db.Set<InvCustomer>().AsNoTracking();
            if (q.Length > 0) query = query.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.City!, like));
            var orders = db.Set<InvSalesOrder>().AsNoTracking();
            rows = await query.OrderBy(x => x.Name)
                .Select(x => new PartyListRow(x.Id, x.Name, x.ContactPerson, x.Phone, x.Email, x.City, x.CreditLimit, x.IsActive == true,
                                              orders.Count(o => o.CustomerId == x.Id)))
                .ToListAsync(ct);
        }
        else
        {
            var query = db.Set<InvSupplier>().AsNoTracking();
            if (q.Length > 0) query = query.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.City!, like));
            var orders = db.Set<InvPurchaseOrder>().AsNoTracking();
            rows = await query.OrderBy(x => x.Name)
                .Select(x => new PartyListRow(x.Id, x.Name, x.ContactPerson, x.Phone, x.Email, x.City, null, x.IsActive == true,
                                              orders.Count(o => o.SupplierId == x.Id)))
                .ToListAsync(ct);
        }
        return c.View("~/Areas/Parties/Views/Parties/List.cshtml", new PartyListModel { Kind = kind, Rows = rows, Q = q });
    }

    private static Dictionary<string, object?> FormValues(IFormCollection form, PartyKind kind) =>
        kind.Columns.ToDictionary(f => f, f => (object?)(form[f].FirstOrDefault() ?? ""));

    private static Dictionary<string, string> Draft(Dictionary<string, object?> raw) =>
        raw.ToDictionary(kv => kv.Key, kv => kv.Value as string ?? "");

    private static Dictionary<string, string> Saved(object entity, PartyKind kind)
    {
        var d = new Dictionary<string, string>();
        switch (entity)
        {
            case InvCustomer x:
                d["name"] = x.Name; d["contact_person"] = x.ContactPerson ?? ""; d["email"] = x.Email ?? ""; d["phone"] = x.Phone ?? "";
                d["mobile"] = x.Mobile ?? ""; d["city"] = x.City ?? ""; d["tax_id"] = x.TaxId ?? ""; d["payment_terms"] = x.PaymentTerms ?? "";
                d["website"] = x.Website ?? ""; d["address"] = x.Address ?? ""; d["notes"] = x.Notes ?? "";
                // {{ f.credit_limit or '0' }} of a float column: "50000.0", and "0" when zero.
                d["credit_limit"] = x.CreditLimit is decimal cl && cl != 0 ? TrackingRules.PyFloat(cl) : "0";
                break;
            case InvSupplier s:
                d["name"] = s.Name; d["contact_person"] = s.ContactPerson ?? ""; d["email"] = s.Email ?? ""; d["phone"] = s.Phone ?? "";
                d["mobile"] = s.Mobile ?? ""; d["city"] = s.City ?? ""; d["tax_id"] = s.TaxId ?? ""; d["payment_terms"] = s.PaymentTerms ?? "";
                d["website"] = s.Website ?? ""; d["address"] = s.Address ?? ""; d["notes"] = s.Notes ?? "";
                break;
        }
        _ = kind;
        return d;
    }

    private static void Apply(object entity, PartyValues v)
    {
        switch (entity)
        {
            case InvCustomer x:
                x.Name = v["name"]; x.ContactPerson = v["contact_person"]; x.Email = v["email"]; x.Phone = v["phone"]; x.Mobile = v["mobile"];
                x.Address = v["address"]; x.City = v["city"]; x.TaxId = v["tax_id"]; x.PaymentTerms = v["payment_terms"];
                x.Website = v["website"]; x.Notes = v["notes"]; x.CreditLimit = v.CreditLimit ?? 0m;
                break;
            case InvSupplier s:
                s.Name = v["name"]; s.ContactPerson = v["contact_person"]; s.Email = v["email"]; s.Phone = v["phone"]; s.Mobile = v["mobile"];
                s.Address = v["address"]; s.City = v["city"]; s.TaxId = v["tax_id"]; s.PaymentTerms = v["payment_terms"];
                s.Website = v["website"]; s.Notes = v["notes"];
                break;
        }
    }

    /// <summary>A new customer / supplier from cleaned values, with its ledger account (in the caller's transaction).</summary>
    private async Task<(int Id, string Name)> CreateAsync(PartyKind kind, PartyValues v, CancellationToken ct)
    {
        object entity = kind.CreditLimit
            ? new InvCustomer { IsActive = true, CreatedAt = clock.UtcNow }
            : new InvSupplier { IsActive = true, CreatedAt = clock.UtcNow };
        Apply(entity, v);
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        int id = entity is InvCustomer c ? c.Id : ((InvSupplier)entity).Id;
        string name = entity is InvCustomer c2 ? c2.Name : ((InvSupplier)entity).Name;
        await ledger.EntityAccountAsync(kind.Key, id, name, ct);
        await db.SaveChangesAsync(ct);
        return (id, name);
    }

    public IActionResult NewForm(Controller c, PartyKind kind) =>
        c.View(FormView, new PartyFormModel { Kind = kind });

    public async Task<IActionResult> CreateAsync(Controller c, PartyKind kind, CancellationToken ct)
    {
        var raw = FormValues(c.Request.Form, kind);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (values, errors) = await MasterData.CleanPartyAsync(db, kind, raw, ct: ct);
        if (errors.Count > 0)
        {
            // Shown again with what was typed: a blank or duplicate name is refused, not saved.
            c.Flash(string.Join(" ", errors), "error");
            return c.View(FormView, new PartyFormModel { Kind = kind, Fields = Draft(raw) });
        }
        var (_, name) = await CreateAsync(kind, values, ct);
        await tx.CommitAsync(ct);
        c.Flash($"{kind.Title} created — ledger account '{name}' added under {kind.Ledger}", "success");
        return c.Redirect(kind.Prefix + "/");
    }

    private async Task<object?> FindAsync(PartyKind kind, int id, bool lockIt, CancellationToken ct)
    {
        if (lockIt)
            return kind.CreditLimit ? await db.LockAsync<InvCustomer>(id, ct) : await db.LockAsync<InvSupplier>(id, ct);
        return kind.CreditLimit
            ? await db.Set<InvCustomer>().FirstOrDefaultAsync(x => x.Id == id, ct)
            : await db.Set<InvSupplier>().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IActionResult> EditFormAsync(Controller c, PartyKind kind, int id, CancellationToken ct)
    {
        var entity = await FindAsync(kind, id, false, ct);
        if (entity is null) return c.NotFound();
        return c.View(FormView, new PartyFormModel
        {
            Kind = kind, Id = id, Fields = Saved(entity, kind),
            IsActive = (entity as InvCustomer)?.IsActive ?? ((InvSupplier)entity).IsActive ?? false,
        });
    }

    public async Task<IActionResult> EditAsync(Controller c, PartyKind kind, int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entity = await FindAsync(kind, id, true, ct);
        if (entity is null) return c.NotFound();
        var raw = FormValues(c.Request.Form, kind);
        bool active = c.Request.Form["is_active"].FirstOrDefault() == "on";
        var (values, errors) = await MasterData.CleanPartyAsync(db, kind, raw, id, ct);
        if (errors.Count > 0)
        {
            c.Flash(string.Join(" ", errors), "error");
            return c.View(FormView, new PartyFormModel { Kind = kind, Id = id, Fields = Draft(raw), IsActive = active });
        }
        Apply(entity, values);
        switch (entity)
        {
            case InvCustomer x: x.IsActive = active; break;
            case InvSupplier s: s.IsActive = active; break;
        }
        await db.SaveChangesAsync(ct);
        await ledger.EntityAccountAsync(kind.Key, id, values["name"], ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        c.Flash($"{kind.Title} updated", "success");
        return c.Redirect(kind.Prefix + "/");
    }

    public async Task<IActionResult> DeleteAsync(Controller c, PartyKind kind, int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entity = await FindAsync(kind, id, false, ct);
        if (entity is null) return c.NotFound();
        bool used = kind.CreditLimit
            ? await db.Set<InvSalesOrder>().AnyAsync(o => o.CustomerId == id, ct)
              || await db.Set<InvInvoice>().AnyAsync(i => i.CustomerId == id, ct)
              || await db.Set<InvSalesReturn>().AnyAsync(r => r.CustomerId == id, ct)
            : await db.Set<InvPurchaseOrder>().AnyAsync(o => o.SupplierId == id, ct)
              || await db.Set<InvPurchaseInvoice>().AnyAsync(i => i.SupplierId == id, ct)
              || await db.Set<InvPurchaseReturn>().AnyAsync(r => r.SupplierId == id, ct);
        if (used || await ledger.EntityHasPostingsAsync(kind.Key, id, ct))
            c.Flash(kind.CreditLimit
                ? "Cannot delete a customer with sales history or ledger entries"
                : "Cannot delete a supplier with purchase history or ledger entries", "error");
        else
        {
            db.Remove(entity);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            c.Flash($"{kind.Title} deleted", "success");
        }
        return c.Redirect(kind.Prefix + "/");
    }

    public async Task<IActionResult> CheckAsync(Controller c, PartyKind kind, string? value, int? exclude, CancellationToken ct)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return c.Json(new { exists = false });
        return c.Json(new { exists = await MasterData.TakenAsync(db, kind, value, exclude, ct) });
    }

    public IActionResult Template(Controller c, PartyKind kind)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(kind.TitlePlural);
        object[][] rows = kind.CreditLimit
            ?
            [
                ["Name", "Contact Person", "Email", "Phone", "Mobile", "City", "Tax ID", "Payment Terms", "Credit Limit", "Website", "Address", "Notes"],
                ["Sample Customer A", "John Doe", "john@example.com", "021-1234567", "0300-1234567", "Karachi", "NTN-1234567", "Net 30", "50000", "https://example.com", "123 Main St", "Regular customer"],
                ["Sample Customer B", "Jane Smith", "jane@example.com", "042-7654321", "0301-7654321", "Lahore", "", "Due on Receipt", "25000", "", "456 Oak Ave", ""],
                ["Sample Customer C", "", "", "", "", "Islamabad", "", "Net 15", "10000", "", "", ""],
            ]
            :
            [
                ["Name", "Contact Person", "Email", "Phone", "Mobile", "City", "Tax ID", "Payment Terms", "Website", "Address", "Notes"],
                ["Sample Supplier A", "Ali Khan", "ali@supplier.com", "021-1112222", "0300-1112222", "Karachi", "NTN-7654321", "Net 30", "https://supplier-a.com", "789 Industrial Rd", ""],
                ["Sample Supplier B", "Sara Ahmed", "sara@supplier.com", "042-3334444", "0301-3334444", "Lahore", "", "Net 45", "", "321 Trade Ave", "Preferred vendor"],
                ["Sample Supplier C", "", "", "", "", "Islamabad", "", "Due on Receipt", "", "", ""],
            ];
        for (int r = 0; r < rows.Length; r++)
            for (int col = 0; col < rows[r].Length; col++)
                if (rows[r][col] is string s && s.Length > 0)
                    ws.Cell(r + 1, col + 1).Value = s;
        double[] widths = kind.CreditLimit
            ? [28, 18, 24, 14, 14, 14, 16, 16, 12, 22, 24, 24]
            : [28, 18, 24, 14, 14, 14, 16, 16, 22, 24, 24];
        for (int i = 0; i < widths.Length; i++)
            ws.Column(i + 1).Width = widths[i];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return c.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                      $"{kind.Key}_import_template.xlsx");
    }

    private IActionResult Review(Controller c, PartyKind kind, List<PartyValues> rows, int created = 0) =>
        c.View(BatchView, new BatchModel { Kind = kind, Rows = rows, Created = created });

    public async Task<IActionResult> UploadAsync(Controller c, PartyKind kind, CancellationToken ct)
    {
        var file = c.Request.Form.Files.GetFile("file");
        if (file is null || string.IsNullOrEmpty(file.FileName))
        {
            c.Flash("No file selected.", "error");
            return c.Redirect(kind.Prefix + "/");
        }
        List<PartyValues> rows;
        try
        {
            await using var stream = file.OpenReadStream();
            rows = await MasterData.ImportRowsAsync(db, kind, MasterData.ReadSheet(stream), ct);
        }
        catch (MasterDataInvalid e)
        {
            c.Flash(e.Message, "error");
            return c.Redirect(kind.Prefix + "/");
        }
        if (rows.Count == 0)
        {
            c.Flash($"The sheet has no {kind.Plural} under its heading row.", "error");
            return c.Redirect(kind.Prefix + "/");
        }
        // The review screen is this response: the rows never ride in the session cookie.
        return Review(c, kind, rows);
    }

    public IActionResult BatchForm(Controller c, PartyKind kind) => Review(c, kind, []);

    public async Task<IActionResult> BatchSaveAsync(Controller c, PartyKind kind, CancellationToken ct)
    {
        List<Dictionary<string, object?>> rows;
        try
        {
            rows = MasterData.SubmittedRows(c.Request.Form["data"].FirstOrDefault());
        }
        catch (MasterDataInvalid e)
        {
            c.Flash(e.Message, "error");
            return c.Redirect(kind.Prefix + "/");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (created, failed) = await MasterData.SaveRowsAsync(db, kind, rows, v => CreateAsync(kind, v, ct), ct);
        await tx.CommitAsync(ct);
        if (failed.Count > 0)
            return Review(c, kind, failed, created);
        c.Flash($"{created} {kind.Key}(s) created.", "success");
        return c.Redirect(kind.Prefix + "/");
    }

}
