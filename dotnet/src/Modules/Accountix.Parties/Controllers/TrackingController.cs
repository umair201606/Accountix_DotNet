using System.Globalization;
using System.Text.Json;
using Accountix.Parties.Services;
using static Accountix.Parties.Services.TrackingRules;

namespace Accountix.Parties.Controllers;

/// <summary>Per-family wording of the tracking pages (tracking.py DOC_META).</summary>
public sealed record DocMeta(string Title, string Kicker, string Blurb, string PartyLabel, string PartyPlural, string MoneyWord,
                             string OpenWord, string Endpoint, string Flow)
{
    public static readonly IReadOnlyDictionary<string, DocMeta> For = new Dictionary<string, DocMeta>
    {
        [Sales] = new("Sales Invoice Tracking", "Invoicing / Receivables",
            "Every approved sales invoice with what has been received against it, what is still outstanding, and how late it is.",
            "Customer", "customers", "Received", "Receivable", "/invoicing/tracking/sales", "receipt"),
        [Purchase] = new("Purchase Invoice Tracking", "Invoicing / Payables",
            "Every approved purchase invoice with what has been paid against it, what is still outstanding, and how late it is.",
            "Supplier", "suppliers", "Paid", "Payable", "/invoicing/tracking/purchases", "payment"),
    };
}

/// <summary>Where a list page sits (tracking.py _page): 100 rows a page.</summary>
public sealed record Pager(int Page, int Pages, int Total, int First, int Last);

public sealed class FeedPageModel
{
    public IReadOnlyList<FeedRow> Rows { get; init; } = [];
    public required Pager Pager { get; init; }
    public int Matchable { get; init; }
    public (int Count, decimal Value) MatchIn { get; init; }
    public (int Count, decimal Value) MatchOut { get; init; }
    public string MatchScope { get; init; } = "";
    public required FeedSummary Summary { get; init; }
    public int Drafts { get; init; }
    public IReadOnlyList<StaleRow> Stale { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Customers { get; init; } = [];
    public IReadOnlyList<(int Id, string Name)> Suppliers { get; init; } = [];
    public IReadOnlyList<(int Id, string Name, string Kind, string Code)> LedgerCustomers { get; init; } = [];
    public IReadOnlyList<(int Id, string Name, string Kind, string Code)> LedgerSuppliers { get; init; } = [];
    public IReadOnlyList<string> States { get; init; } = [];
    public string Flow { get; init; } = "";
    public string Side { get; init; } = "";
    public string Party { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Q { get; init; } = "";
    public bool CanAssign { get; init; }
    public bool Filtering => Flow.Length > 0 || Side.Length > 0 || Party.Length > 0 || Q.Length > 0 || From.Length > 0 || To.Length > 0;
}

public sealed class AssignPageModel
{
    public required FeedRow Row { get; init; }
    public IReadOnlyList<InvoiceRow> Invoices { get; init; } = [];
    public IReadOnlyList<SplitRow> Splits { get; init; } = [];
    public required DocMeta DocMeta { get; init; }
    public bool CanAssign { get; init; }
}

public sealed class InvoicesPageModel
{
    public required string DocType { get; init; }
    public required DocMeta DocMeta { get; init; }
    public IReadOnlyList<InvoiceRow> Rows { get; init; } = [];
    public required Pager Pager { get; init; }
    public string Sort { get; init; } = "newest";
    public decimal WaitingTotal { get; init; }
    public int WaitingParties { get; init; }
    public required OutstandingSummary Summary { get; init; }
    public IReadOnlyList<(int Id, string Name)> Parties { get; init; } = [];
    public IReadOnlyList<string> Pay { get; init; } = [];
    public IReadOnlyList<string> Age { get; init; } = [];
    public bool Overdue { get; init; }
    public int? PartyId { get; init; }
    public string Q { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public bool CanAssign { get; init; }
    public bool Filtering => PartyId is int p && p != 0 || Q.Length > 0 || From.Length > 0 || To.Length > 0 || Age.Count > 0 || Overdue;
}

public sealed class HistoryPageModel
{
    public required string DocType { get; init; }
    public required DocMeta DocMeta { get; init; }
    public required InvoiceRow Row { get; init; }
    public string ReturnPath { get; init; } = "";
    public IReadOnlyList<HistoryEvent> History { get; init; } = [];
    public IReadOnlyList<FeedRow> Available { get; init; } = [];
    public string EditUrl { get; init; } = "";
    public bool CanAssign { get; init; }
}

/// <summary>Invoicing → Invoice Tracking (invoicing_app/routes/tracking.py, blueprint inv_tracking).</summary>
[Area("Parties")]
[Module(Modules.Invoicing)]
[Route("invoicing/tracking")]
public sealed class TrackingController(PaymentTracking pt, AccountixDbContext db) : Controller
{
    private const string Resource = "payment_tracking";
    public const int PerPage = 100;

    private EffectiveAccess Access => HttpContext.Access()!;
    private bool CanAssign => Access.Can(Resource, Rights.Edit);

    // ── query-string helpers ────────────────────────────────────────────

    /// <summary>_parse_date: Y-m-d, d/m/Y or m/d/Y; null when none fits.</summary>
    public static DateTime? ParseDate(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return null;
        foreach (string fmt in new[] { "yyyy-M-d", "d/M/yyyy", "M/d/yyyy" })
            if (DateTime.TryParseExact(value, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    private static DateTime? Eod(DateTime? d) => d?.Date.AddHours(23).AddMinutes(59).AddSeconds(59);

    /// <summary>_split_party: "customer:12" → ("customer", 12); negative ids are ledger parties.</summary>
    public static (string Kind, int? Id) SplitParty(string? raw)
    {
        raw ??= "";
        int colon = raw.IndexOf(':');
        string kind = colon < 0 ? raw : raw[..colon];
        string ident = colon < 0 ? "" : raw[(colon + 1)..];
        if (kind is not ("customer" or "supplier")) return ("", null);
        ident = ident.Trim();
        string digits = ident.StartsWith('-') ? ident[1..] : ident;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !int.TryParse(ident, CultureInfo.InvariantCulture, out int id))
            return ("", null);
        return (kind, id);
    }

    /// <summary>_selected: multi-select values; present-but-empty means "all".</summary>
    private List<string> Selected(string name, IEnumerable<string> allowed, IEnumerable<string>? fallback = null)
    {
        if (!Request.Query.ContainsKey(name)) return (fallback ?? []).ToList();
        var ok = allowed.ToHashSet();
        return Request.Query[name].Where(v => v is not null && ok.Contains(v)).Select(v => v!).ToList();
    }

    private (List<T> Rows, Pager Pager) Page<T>(List<T> rows)
    {
        int total = rows.Count;
        int pages = Math.Max(1, (total + PerPage - 1) / PerPage);
        int page = int.TryParse(Request.Query["page"].FirstOrDefault(), out int p) && p != 0 ? p : 1;
        page = Math.Min(Math.Max(page, 1), pages);
        int start = (page - 1) * PerPage;
        return (rows.Skip(start).Take(PerPage).ToList(),
                new Pager(page, pages, total, total > 0 ? start + 1 : 0, Math.Min(start + PerPage, total)));
    }

    private async Task<List<(int Id, string Name)>> PartyListAsync(string kind, CancellationToken ct) =>
        (await (kind == "customer" ? pt.CustomersAsync(ct) : pt.SuppliersAsync(ct))).Values
        .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (p.Id, p.Name)).ToList();

    // ── Feed ───────────────────────────────────────────────────────────

    [HttpGet("")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> Feed(CancellationToken ct)
    {
        var states = Selected("state", AssignStates, DefaultAssignStates);
        string flow = Request.Query["flow"].FirstOrDefault() ?? "";
        flow = flow is "receipt" or "payment" ? flow : "";
        var from = ParseDate(Request.Query["from"]);
        var to = Eod(ParseDate(Request.Query["to"]));
        string q = (Request.Query["q"].FirstOrDefault() ?? "").Trim();
        string side = Request.Query["side"].FirstOrDefault() ?? "";
        side = side is "customer" or "supplier" ? side : "";
        string partyRaw = Request.Query["party"].FirstOrDefault() ?? "";
        var (partyType, partyId) = SplitParty(partyRaw);
        if (partyType.Length > 0 && side.Length > 0 && partyType != side)
            (partyRaw, partyType, partyId) = ("", "", null);
        if (partyType.Length > 0) side = partyType;
        if (side.Length > 0) flow = side == "customer" ? "receipt" : "payment";

        // One read of the books in one snapshot: every row once, narrowed here.
        await using var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var (all, stale) = await pt.FeedSnapshotAsync(ct);
        var rows = PaymentTracking.FilterFeed(all, new FeedFilter
        {
            States = states.Count > 0 ? states : null, Flow = flow.Length > 0 ? flow : null,
            PartyType = side.Length > 0 ? side : null, PartyId = partyId, From = from, To = to, Search = q.Length > 0 ? q : null,
        });
        var matchable = PaymentTracking.FilterFeed(all, new FeedFilter
        {
            States = ["unassigned", "partial"], PartyType = side.Length > 0 ? side : null, PartyId = partyId,
        }).Where(r => !r.IsLedger).ToList();
        var (pageRows, pager) = Page(rows);
        string scope = (partyId is int pid && pid != 0 ? all.FirstOrDefault(r => r.PartyId == pid)?.PartyName : null) is { Length: > 0 } n
            ? n
            : side switch { "customer" => "all customers", "supplier" => "all suppliers", _ => "all customers and suppliers" };
        var model = new FeedPageModel
        {
            Rows = pageRows,
            Pager = pager,
            Matchable = matchable.Count,
            MatchIn = (matchable.Count(r => r.Flow == "receipt"), matchable.Where(r => r.Flow == "receipt").Sum(r => r.Unassigned)),
            MatchOut = (matchable.Count(r => r.Flow == "payment"), matchable.Where(r => r.Flow == "payment").Sum(r => r.Unassigned)),
            MatchScope = scope,
            Summary = FeedSummary.Of(all),
            Drafts = await pt.DraftSettlementCountAsync(ct),
            Stale = await pt.StaleAllocationsAsync(stale, ct),
            Customers = await PartyListAsync("customer", ct),
            Suppliers = await PartyListAsync("supplier", ct),
            LedgerCustomers = await pt.LedgerPartiesAsync("customer", ct),
            LedgerSuppliers = await pt.LedgerPartiesAsync("supplier", ct),
            States = states,
            Flow = flow,
            Side = side,
            Party = partyRaw,
            From = Request.Query["from"].FirstOrDefault() ?? "",
            To = Request.Query["to"].FirstOrDefault() ?? "",
            Q = q,
            CanAssign = CanAssign,
        };
        await snap.CommitAsync(ct);
        return View(model);
    }

    [HttpGet("assign/{line_id:int}")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> Assign([FromRoute(Name = "line_id")] int lineId, CancellationToken ct)
    {
        var row = await pt.FeedRowAsync(lineId, ct);
        if (row is null)
        {
            this.Flash("Only approved receipts and payments can be assigned.", "error");
            return Redirect("/invoicing/tracking/");
        }
        return View(new AssignPageModel
        {
            Row = row,
            Invoices = await pt.OpenInvoicesAsync(row.PartyType, row.PartyId, lineId, ct: ct),
            Splits = await pt.HistoryForLineAsync(lineId, ct),
            DocMeta = DocMeta.For[row.DocType],
            CanAssign = CanAssign,
        });
    }

    // ── Invoice tracking (sales / purchase) ─────────────────────────────

    public static readonly (string Key, string Label)[] Sorts =
        [("newest", "Newest first"), ("oldest", "Oldest first"), ("late", "Most overdue"), ("owed", "Largest outstanding")];

    private static List<InvoiceRow> SortRows(List<InvoiceRow> rows, string sort) => sort switch
    {
        "oldest" => rows.OrderBy(r => r.Date ?? DateOnly.MinValue).ThenBy(r => r.Id).ToList(),
        "late" => rows.OrderByDescending(r => r.OverdueDays).ThenByDescending(r => r.Outstanding).ToList(),
        "owed" => rows.OrderByDescending(r => r.Outstanding).ThenByDescending(r => r.OverdueDays).ToList(),
        _ => rows.OrderByDescending(r => r.Date ?? DateOnly.MinValue).ThenByDescending(r => r.Id).ToList(),
    };

    private async Task<IActionResult> TrackingPage(string docType, CancellationToken ct)
    {
        var meta = DocMeta.For[docType];
        var pay = Selected("pay", PayStates, ["unpaid", "partial"]);
        string sort = Request.Query["sort"].FirstOrDefault() ?? "newest";
        sort = Sorts.Any(s => s.Key == sort) ? sort : "newest";
        var age = Selected("age", AgeBuckets.Select(b => b.Key));
        bool overdue = Request.Query["overdue"] == "1";
        int? partyId = int.TryParse(Request.Query["party_id"].FirstOrDefault(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int p) ? p : null;
        string q = (Request.Query["q"].FirstOrDefault() ?? "").Trim();
        var from = ParseDate(Request.Query["from"]);
        var to = Eod(ParseDate(Request.Query["to"]));

        await using var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var all = await pt.InvoiceRowsAsync(docType, ct: ct);
        var rows = PaymentTracking.FilterInvoices(all, new InvoiceFilter
        {
            PartyId = partyId, PayStates = pay.Count > 0 ? pay : null, AgeBuckets = age.Count > 0 ? age : null,
            OverdueOnly = overdue, Search = q.Length > 0 ? q : null, From = from, To = to,
        });
        rows = SortRows(rows, sort);
        var (pageRows, pager) = Page(rows);
        string kind = PartyForDoc[docType];
        var waiting = new Dictionary<int, decimal>();
        foreach (var item in await pt.FeedRowsAsync(new FeedFilter { States = ["unassigned", "partial"], PartyType = kind }, ct: ct))
        {
            if (item.IsLedger) continue;
            waiting[item.PartyId] = waiting.GetValueOrDefault(item.PartyId) + item.Unassigned;
        }
        var model = new InvoicesPageModel
        {
            DocType = docType, DocMeta = meta, Rows = pageRows, Pager = pager, Sort = sort,
            WaitingTotal = waiting.Values.Sum(), WaitingParties = waiting.Count,
            Summary = OutstandingSummary.Of(all),
            Parties = await PartyListAsync(kind, ct),
            Pay = pay, Age = age, Overdue = overdue, PartyId = partyId, Q = q,
            From = Request.Query["from"].FirstOrDefault() ?? "", To = Request.Query["to"].FirstOrDefault() ?? "",
            CanAssign = CanAssign,
        };
        await snap.CommitAsync(ct);
        return View("Invoices", model);
    }

    [HttpGet("sales")]
    [Right(Resource, Rights.View)]
    public Task<IActionResult> SalesTracking(CancellationToken ct) => TrackingPage(Sales, ct);

    [HttpGet("purchases")]
    [Right(Resource, Rights.View)]
    public Task<IActionResult> PurchaseTracking(CancellationToken ct) => TrackingPage(Purchase, ct);

    [HttpGet("invoice/{doc_type}/{doc_id:int}")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> InvoiceHistory([FromRoute(Name = "doc_type")] string docType, [FromRoute(Name = "doc_id")] int docId,
                                                    CancellationToken ct)
    {
        if (docType is not (Sales or Purchase))
        {
            this.Flash("Unknown invoice type.", "error");
            return Redirect("/invoicing/tracking/");
        }
        var row = await pt.InvoiceRowAsync(docType, docId, ct: ct);
        if (row is null)
        {
            this.Flash("Invoice not found.", "error");
            return Redirect(DocMeta.For[docType].Endpoint);
        }
        return View(new HistoryPageModel
        {
            DocType = docType,
            DocMeta = DocMeta.For[docType],
            Row = row,
            ReturnPath = docType == Sales ? "/invoicing/sales-return/" : "/inventory/purchase-return/",
            History = await pt.HistoryForDocAsync(docType, docId, ct),
            Available = await pt.FeedRowsAsync(new FeedFilter { States = ["unassigned", "partial"], PartyType = row.PartyType, PartyId = row.PartyId }, ct: ct),
            EditUrl = docType == Sales ? $"/inventory/invoices/{docId}" : $"/inventory/purchase-invoice/{docId}",
            CanAssign = CanAssign,
        });
    }

    // ── JSON API ───────────────────────────────────────────────────────

    /// <summary>request.get_json(silent=True) or {}: the body's object, or an empty one.</summary>
    private async Task<JsonElement> BodyAsync(CancellationToken ct)
    {
        if (!Request.HasJsonContentType()) return default;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static JsonElement? Get(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) ? v : null;

    private static string Str(JsonElement body, string name) =>
        Get(body, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

    /// <summary>A JSON value as Python's json gives it (number: int, else the exact decimal text).</summary>
    private static object? Value(JsonElement? e)
    {
        if (e is not JsonElement v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long l) ? l : v.TryGetDecimal(out decimal d) ? d : v.GetDouble(),
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => v.GetRawText(),
        };
    }

    private IActionResult Fail(string message, int status = 400) => StatusCode(status, new { ok = false, error = message });

    private async Task<object> SplitsJsonAsync(int lineId, CancellationToken ct) =>
        (await pt.HistoryForLineAsync(lineId, ct)).Select(s => s.ToJson()).ToList();

    [HttpGet("api/feed-item/{line_id:int}")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> ApiFeedItem([FromRoute(Name = "line_id")] int lineId, CancellationToken ct)
    {
        var row = await pt.FeedRowAsync(lineId, ct);
        if (row is null) return Fail("Not an assignable payment.", 404);
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["row"] = row.ToJson(),
            ["invoices"] = (await pt.OpenInvoicesAsync(row.PartyType, row.PartyId, lineId, ct: ct)).Select(i => i.ToJson()).ToList(),
            ["splits"] = await SplitsJsonAsync(lineId, ct),
        });
    }

    [HttpGet("api/auto-split/{line_id:int}")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> ApiAutoSplit([FromRoute(Name = "line_id")] int lineId, CancellationToken ct) =>
        Json(new
        {
            ok = true,
            split = (await pt.AutoSplitAsync(lineId, ct)).Select(s => new { doc_id = s.DocId, number = s.Number, amount = (double)s.Amount }),
        });

    [HttpPost("api/assign/{line_id:int}")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiAssign([FromRoute(Name = "line_id")] int lineId, CancellationToken ct)
    {
        var body = await BodyAsync(ct);
        var entries = new List<PaymentTracking.AllocationEntry>();
        if (Get(body, "entries") is { ValueKind: JsonValueKind.Array } list)
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    return Fail("Invalid invoice or amount in the assignment.");
                entries.Add(new(Value(Get(e, "doc_id")), Value(Get(e, "amount"))));
            }
        FeedRow? row;
        try
        {
            row = await pt.AllocateAsync(lineId, entries, Access.User.Id, Str(body, "note").Trim(), ct);
        }
        catch (AllocationException e)
        {
            return Fail(e.Message);
        }
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true, ["row"] = row?.ToJson(), ["splits"] = await SplitsJsonAsync(lineId, ct), ["message"] = "Assignment saved",
        });
    }

    [HttpPost("api/auto-match")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiAutoMatch(CancellationToken ct)
    {
        var body = await BodyAsync(ct);
        var (partyType, partyId) = SplitParty(Str(body, "party"));
        string side = Str(body, "side");
        string? scope = partyType.Length > 0 ? partyType : side is "customer" or "supplier" ? side : null;
        var result = await pt.AutoMatchAsync(scope, partyId, Access.User.Id, ct);
        string message;
        if (result.Lines == 0)
            message = "Nothing to match: no open receipt or payment has an open invoice to settle.";
        else
        {
            static string Count(int n, string word) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {word}{(n == 1 ? "" : "s")}";
            string what = string.Join(" and ", new[] { (result.Receipts, "receipt"), (result.Payments, "payment") }
                .Where(x => x.Item1 != 0).Select(x => Count(x.Item1, x.Item2)));
            message = $"Matched {what} to {Count(result.Invoices, "invoice")}, oldest first.";
        }
        return Json(new
        {
            ok = true, message, lines = result.Lines, receipts = result.Receipts, payments = result.Payments,
            invoices = result.Invoices, amount = (double)result.Amount,
        });
    }

    [HttpPost("api/apply/{line_id:int}/{doc_type}/{doc_id:int}")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiApplyHere([FromRoute(Name = "line_id")] int lineId, [FromRoute(Name = "doc_type")] string docType,
                                                 [FromRoute(Name = "doc_id")] int docId, CancellationToken ct)
    {
        if (docType is not (Sales or Purchase)) return Fail("Unknown invoice type.");
        var row = await pt.FeedRowAsync(lineId, ct);
        var inv = await pt.InvoiceRowAsync(docType, docId, ct: ct);
        if (row is null || inv is null) return Fail("That receipt or invoice is no longer open.", 404);
        decimal already = (await pt.HistoryForLineAsync(lineId, ct))
            .FirstOrDefault(s => !s.Forced && s.DocType == docType && s.DocId == docId)?.Amount ?? 0m;
        decimal take = Math.Min(row.Unassigned, inv.Outstanding);
        if (take <= 0)
            return Fail("Nothing left to apply — the receipt or the invoice is already fully settled.");
        try
        {
            await pt.AllocateAsync(lineId, [new(docId, Math.Round(already + take, 2, MidpointRounding.ToEven))], Access.User.Id, ct: ct);
        }
        catch (AllocationException e)
        {
            return Fail(e.Message);
        }
        return Json(new { ok = true, message = $"Applied {Comma2(take)} to {inv.Number}" });
    }

    [HttpPost("api/force-close/{line_id:int}")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiForceClose([FromRoute(Name = "line_id")] int lineId, CancellationToken ct)
    {
        var body = await BodyAsync(ct);
        FeedRow? row;
        try
        {
            row = await pt.ForceCloseAsync(lineId, Value(Get(body, "amount")), Str(body, "reason").Trim(), Access.User.Id, ct);
        }
        catch (AllocationException e)
        {
            return Fail(e.Message);
        }
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true, ["row"] = row?.ToJson(), ["splits"] = await SplitsJsonAsync(lineId, ct), ["message"] = "Closed as not applicable",
        });
    }

    [HttpPost("api/release-force/{line_id:int}")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiReleaseForce([FromRoute(Name = "line_id")] int lineId, CancellationToken ct)
    {
        FeedRow? row;
        try
        {
            row = await pt.ReleaseForceAsync(lineId, ct);
        }
        catch (AllocationException e)
        {
            return Fail(e.Message);
        }
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true, ["row"] = row?.ToJson(), ["splits"] = await SplitsJsonAsync(lineId, ct), ["message"] = "Reopened for assignment",
        });
    }

    [HttpPost("api/clear-stale")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiClearStale(CancellationToken ct)
    {
        var body = await BodyAsync(ct);
        List<int>? ids = null;
        if (Get(body, "ids") is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
            ids = list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int i) ? i
                : int.TryParse(e.ToString(), out int j) ? j : 0).ToList();
        int cleared = await pt.ClearStaleAsync(ids, ct);
        return Json(new
        {
            ok = true, cleared,
            message = cleared != 0 ? $"Cleared {cleared} broken assignment{(cleared == 1 ? "" : "s")}" : "There was nothing to clear",
        });
    }

    [HttpPost("api/unassign/{alloc_id:int}")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> ApiUnassign([FromRoute(Name = "alloc_id")] int allocId, CancellationToken ct)
    {
        try
        {
            var (docType, docId, lineId) = await pt.UnallocateAsync(allocId, ct);
            return Json(new { ok = true, message = "Assignment removed", doc_type = docType, doc_id = docId, line_id = lineId });
        }
        catch (AllocationException e)
        {
            return Fail(e.Message);
        }
    }

    [HttpGet("api/open-invoices")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> ApiOpenInvoices(CancellationToken ct)
    {
        string partyType = Request.Query["party_type"].FirstOrDefault() ?? "customer";
        if (partyType is not ("customer" or "supplier")) return Fail("Unknown party type.");
        int? partyId = int.TryParse(Request.Query["party_id"].FirstOrDefault(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int p) ? p : null;
        if (partyId is not int pid || pid == 0) return Fail("Party is required.");
        int? lineId = int.TryParse(Request.Query["line_id"].FirstOrDefault(), out int l) ? l : null;
        return Json(new
        {
            ok = true,
            invoices = (await pt.OpenInvoicesAsync(partyType, pid, lineId, ct: ct)).Select(i => i.ToJson()).ToList(),
        });
    }
}

/// <summary>Query-string text as Werkzeug's url_for writes it (spaces as '+', "!$'()*,/:;?@" kept).</summary>
public static class QueryText
{
    public static string Encode(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || "-._~!$'()*,/:;?@".Contains(c))) sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
