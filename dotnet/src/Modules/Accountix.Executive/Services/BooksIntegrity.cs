namespace Accountix.Executive.Services;

/// <summary>One check's answer, as shared/integrity.py's dict {key, label, ok, expected, actual, detail}.</summary>
public sealed record IntegrityCheck(string Key, string Label, bool Ok, decimal Expected, decimal Actual, string Detail);

/// <summary>
/// Books integrity (shared/integrity.py): do the general ledger and the sub-ledgers still agree?
/// Every check of the Python app, for the active company, read-only. Each table is read in ONE
/// grouped query (posted lines by account and label, stock by product, documents with an EXISTS
/// for their journal), so the page costs the same ~25 queries on any amount of data. Run inside
/// the page's REPEATABLE READ snapshot, every check sees the books at the same instant.
/// </summary>
public sealed class BooksIntegrity(AccountixDbContext db)
{
    private const decimal Tol = 0.01m;

    public static readonly IReadOnlyList<string> Keys =
    [
        "trial_balance", "journals_balanced", "reporting_table", "inventory_vs_gl", "stock_layers",
        "stock_quantity", "fixed_assets_vs_gl", "documents_posted", "payroll_vs_gl", "staff_loans_vs_gl", "pf_fund_vs_gl",
    ];

    // ROLE_CODES / POSTING_ACCOUNTS["inventory"], ENTITY_PARENT_CODES["loan"], LEGACY_CODE_MAP (shared/coa.py).
    private const string InventoryRoleCode = "1-01-04-01-0001";
    private static readonly string[] InventoryAlternates = ["1200", "113"];
    private const string LoanParentCode = "1-01-03-01";
    private const int EntityIdOffset = 100;
    private static readonly Dictionary<string, string> LegacyCodes = new() { ["2121"] = "2-01-02-02-0001", ["2123"] = "2-01-02-02-0002" };

    private sealed record Acct(int Id, string Code);

    private List<Acct> _chart = [];
    private Dictionary<int, (decimal Dr, decimal Cr)> _byAccount = [];
    private Dictionary<(int Account, int Label), (decimal Dr, decimal Cr)> _byAccountLabel = [];
    private Dictionary<int, (decimal Qty, decimal Value)>? _stockByProduct;

    private static IntegrityCheck Check(string key, string label, decimal expected, decimal actual, string detail = "", decimal tol = Tol) =>
        new(key, label, Math.Abs(expected - actual) <= tol, expected, actual, detail);

    /// <summary>run_all(): every check; one that cannot run is itself a finding.</summary>
    public async Task<List<IntegrityCheck>> RunAllAsync(CancellationToken ct = default)
    {
        var checks = new (string Key, Func<CancellationToken, Task<IntegrityCheck>> Run)[]
        {
            ("trial_balance", TrialBalanceAsync), ("journals_balanced", JournalsBalancedAsync),
            ("reporting_table", ReportingTableAsync), ("inventory_vs_gl", InventoryVsGlAsync),
            ("stock_layers", StockLayersAsync), ("stock_quantity", StockQuantityAsync),
            ("fixed_assets_vs_gl", FixedAssetsVsGlAsync), ("documents_posted", DocumentsPostedAsync),
            ("payroll_vs_gl", PayrollVsGlAsync), ("staff_loans_vs_gl", StaffLoansVsGlAsync), ("pf_fund_vs_gl", PfFundVsGlAsync),
        };
        var output = new List<IntegrityCheck>();
        try
        {
            await EnsureLedgerAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Without the ledger nothing can be compared: every check says so, as Python's would one by one.
            await RecoverAsync(ct);
            return checks.Select(c => Failed(c.Key, e)).ToList();
        }
        foreach (var (key, run) in checks)
        {
            var tx = db.Database.CurrentTransaction;
            if (tx is not null && tx.SupportsSavepoints)
                await tx.CreateSavepointAsync("integrity_check", ct);
            try
            {
                output.Add(await run(ct));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // On Postgres a failed statement aborts the transaction; go back to before the check.
                if (tx is not null && tx.SupportsSavepoints)
                    await tx.RollbackToSavepointAsync("integrity_check", ct);
                output.Add(Failed(key, e));
            }
        }
        return output;
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is { } tx)
        {
            try { await tx.RollbackAsync(ct); }
            catch (Exception) { /* already gone */ }
        }
    }

    private static IntegrityCheck Failed(string key, Exception e) =>
        new(key, key.Replace('_', ' '), false, 0, 0, $"Could not run: {e.Message}");

    public async Task<List<IntegrityCheck>> FailuresAsync(CancellationToken ct = default) =>
        (await RunAllAsync(ct)).Where(c => !c.Ok).ToList();

    // ── shared reads ─────────────────────────────────────────────────────

    /// <summary>The chart, and every posted line summed by (account, label): the trial balance, every
    /// _gl_balance and the reporting-table comparison all come from this one grouped read.</summary>
    private bool _loaded;

    private async Task EnsureLedgerAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await LoadLedgerAsync(ct);
        _loaded = true;
    }

    private async Task LoadLedgerAsync(CancellationToken ct)
    {
        _chart = await db.Set<ChartOfAccount>().AsNoTracking().OrderBy(a => a.Id).Select(a => new Acct(a.Id, a.Code)).ToListAsync(ct);
        var rows = await db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.JournalEntry.IsPosted == true)
            .GroupBy(l => new { l.AccountId, Label = l.LabelId ?? 0 })
            .Select(g => new { g.Key.AccountId, g.Key.Label, Dr = g.Sum(x => x.Debit ?? 0), Cr = g.Sum(x => x.Credit ?? 0) })
            .ToListAsync(ct);
        _byAccountLabel = rows.ToDictionary(r => (r.AccountId, r.Label), r => (r.Dr, r.Cr));
        _byAccount = rows.GroupBy(r => r.AccountId).ToDictionary(g => g.Key, g => (g.Sum(r => r.Dr), g.Sum(r => r.Cr)));
    }

    /// <summary>_gl_balance(prefixes, extra_ids): net debit of posted lines on accounts under the prefixes.</summary>
    private decimal GlBalance(IReadOnlyCollection<string> prefixes, IEnumerable<int>? extraIds = null)
    {
        var ids = _chart.Where(a => prefixes.Any(p => (a.Code ?? "").StartsWith(p, StringComparison.Ordinal))).Select(a => a.Id).ToHashSet();
        if (extraIds is not null) ids.UnionWith(extraIds);
        decimal dr = 0, cr = 0;
        foreach (int id in ids)
            if (_byAccount.TryGetValue(id, out var b)) { dr += b.Dr; cr += b.Cr; }
        return dr - cr;
    }

    /// <summary>ChartOfAccount.query.filter_by(code=).first(), else the legacy code's new code.</summary>
    private Acct? LegacyAccount(string code) =>
        _chart.FirstOrDefault(a => a.Code == code)
        ?? (LegacyCodes.TryGetValue(code, out var mapped) ? _chart.FirstOrDefault(a => a.Code == mapped) : null);

    /// <summary>_ledger_net_by_product: {product: (net qty, net posted cost)} in one grouped query.</summary>
    private async Task<Dictionary<int, (decimal Qty, decimal Value)>> StockByProductAsync(CancellationToken ct)
    {
        if (_stockByProduct is not null) return _stockByProduct;
        var rows = await db.Set<StockLedger>().AsNoTracking()
            .GroupBy(s => new { s.ProductId, s.TransactionType })
            .Select(g => new { g.Key.ProductId, g.Key.TransactionType, Q = g.Sum(x => x.Quantity), V = g.Sum(x => x.TotalCost) })
            .ToListAsync(ct);
        var output = new Dictionary<int, (decimal, decimal)>();
        foreach (var r in rows.OrderBy(r => r.ProductId))
        {
            int sign = r.TransactionType == "IN" ? 1 : -1;
            var (oq, ov) = output.TryGetValue(r.ProductId, out var o) ? o : (0m, 0m);
            output[r.ProductId] = (oq + sign * r.Q, ov + sign * r.V);
        }
        return _stockByProduct = output;
    }

    // ── the checks ───────────────────────────────────────────────────────

    public async Task<IntegrityCheck> TrialBalanceAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        decimal dr = _byAccount.Values.Sum(b => b.Dr), cr = _byAccount.Values.Sum(b => b.Cr);
        return Check("trial_balance", "Trial balance (debits = credits)", dr, cr, "Total posted debits vs credits");
    }

    public async Task<IntegrityCheck> JournalsBalancedAsync(CancellationToken ct = default)
    {
        var perEntry = db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.JournalEntry.IsPosted == true)
            .GroupBy(l => new { l.JournalEntryId, l.JournalEntry.VoucherNumber })
            .Select(g => new { g.Key.JournalEntryId, g.Key.VoucherNumber, Dr = g.Sum(x => x.Debit ?? 0), Cr = g.Sum(x => x.Credit ?? 0) });
        int checkedCount = await perEntry.CountAsync(ct);
        var bad = await perEntry.Where(e => e.Dr - e.Cr > Tol || e.Cr - e.Dr > Tol)
            .OrderBy(e => e.JournalEntryId).Select(e => e.VoucherNumber).ToListAsync(ct);
        return new IntegrityCheck("journals_balanced", "Every journal balances", bad.Count == 0, 0, bad.Count,
            bad.Count > 0 ? "Unbalanced: " + string.Join(", ", bad.Take(10)) : $"{checkedCount} posted journals checked");
    }

    public async Task<IntegrityCheck> ReportingTableAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        var summary = (await db.Set<GlDailyBalance>().AsNoTracking()
                .GroupBy(g => new { g.AccountId, g.LabelId })
                .Select(g => new { g.Key.AccountId, g.Key.LabelId, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
                .ToListAsync(ct))
            .ToDictionary(r => (r.AccountId, r.LabelId), r => (r.Dr, r.Cr));
        int bad = 0;
        foreach (var key in _byAccountLabel.Keys.Union(summary.Keys))
        {
            var s = _byAccountLabel.TryGetValue(key, out var a) ? a : (0m, 0m);
            var m = summary.TryGetValue(key, out var b) ? b : (0m, 0m);
            if (Math.Abs(s.Item1 - m.Item1) > Tol || Math.Abs(s.Item2 - m.Item2) > Tol) bad++;
        }
        return Check("reporting_table", "Reporting table matches the ledger", 0, bad,
            "Accounts whose report totals differ from their journal lines" + (bad > 0 ? $" — run `dotnet Accountix.Web.dll admin rebuild-gl-summary --apply` ({bad} differ)" : ""),
            tol: 0);
    }

    public async Task<IntegrityCheck> InventoryVsGlAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        // posting_account("inventory"): its role code, else a legacy code (read only: never created here).
        var inventory = _chart.FirstOrDefault(a => a.Code == InventoryRoleCode)
                        ?? InventoryAlternates.Select(c => _chart.FirstOrDefault(a => a.Code == c)).FirstOrDefault(a => a is not null);
        decimal gl = GlBalance(["1-01-04"], inventory is null ? null : [inventory.Id]);
        var stock = await StockByProductAsync(ct);
        decimal valuation = stock.Values.Sum(v => v.Value);
        string detail = "General-ledger inventory balance vs the stock ledger's value";
        if (Math.Abs(valuation - gl) > Tol)
        {
            // unjournaled_openings(): OPENING rows whose product has no posted OPENING journal.
            var rows = await db.Set<StockLedger>().AsNoTracking()
                .Where(s => s.VoucherType == "OPENING" && s.TransactionType == "IN" && s.TotalCost != 0
                            && !db.Set<JournalEntry>().Any(e => e.VoucherType == "OPENING" && e.IsPosted == true && e.VoucherId == s.ProductId))
                .Select(s => s.TotalCost).ToListAsync(ct);
            if (rows.Count > 0)
                detail += $" — {rows.Count} opening-stock rows worth {Py.Comma2(rows.Sum())} have no journal"
                          + " (stock brought in before opening stock posted to the ledger);"
                          + " `dotnet Accountix.Web.dll admin repair-opening-stock --apply` posts them against Opening Balance Equity";
        }
        return Check("inventory_vs_gl", "Inventory account = stock valuation", valuation, gl, detail);
    }

    public async Task<IntegrityCheck> StockLayersAsync(CancellationToken ct = default)
    {
        var ledger = await StockByProductAsync(ct);
        var layerValue = (await db.Set<StockLayer>().AsNoTracking().Where(l => l.QtyRemaining != 0)
                .GroupBy(l => l.ProductId).Select(g => new { g.Key, V = g.Sum(x => x.ValueRemaining) }).ToListAsync(ct))
            .ToDictionary(r => r.Key, r => r.V);
        // Only products whose grouped figures disagree get the exact check (assert_invariant: the layers
        // against the running cost of the product's latest row, which clamps to zero at zero quantity).
        var suspects = ledger.Where(p => Math.Abs(layerValue.GetValueOrDefault(p.Key) - p.Value.Value) > Tol).Select(p => p.Key).ToList();
        var bad = new List<string>();
        if (suspects.Count > 0)
        {
            var latest = await LatestRunningCostAsync(suspects, ct);
            foreach (int pid in suspects)
            {
                decimal layers = layerValue.TryGetValue(pid, out var v) ? v : 0m;
                decimal running = latest.GetValueOrDefault(pid);
                if (Math.Abs(layers - running) > Tol)
                    bad.Add($"#{pid} layers {Py.DecStr(layers)} vs ledger {Py.DecStr(running)}");
            }
        }
        return new IntegrityCheck("stock_layers", "Cost layers back the stock ledger", bad.Count == 0, 0, bad.Count,
            bad.Count > 0 ? string.Join("; ", bad.Take(5)) : $"{ledger.Count} products checked");
    }

    /// <summary>StockLedger.get_running_balance's cost for several products in one query: the latest row by document date.</summary>
    internal async Task<Dictionary<int, decimal>> LatestRunningCostAsync(IReadOnlyCollection<int> productIds, CancellationToken ct = default)
    {
        var ids = productIds.ToArray();
        return (await db.Set<StockLedger>().AsNoTracking().Where(s => ids.Contains(s.ProductId))
                .GroupBy(s => s.ProductId)
                .Select(g => g.OrderByDescending(s => s.TxnDate).ThenByDescending(s => s.Id).Select(s => new { s.ProductId, s.RunningCost }).First())
                .ToListAsync(ct))
            .ToDictionary(r => r.ProductId, r => r.RunningCost);
    }

    public async Task<IntegrityCheck> StockQuantityAsync(CancellationToken ct = default)
    {
        var ledger = await StockByProductAsync(ct);
        var products = await db.Set<InvProduct>().AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.CurrentStock }).ToListAsync(ct);
        var bad = new List<string>();
        foreach (var p in products)
        {
            if (!ledger.TryGetValue(p.Id, out var l)) continue;
            // The Quantity column type reads the figure back rounded to 4 places.
            decimal shown = p.CurrentStock is decimal cs ? Py.FromDouble(Py.Round(Py.ToDouble(cs), 4)) : 0m;
            if (Math.Abs(l.Qty - shown) > 0.0001m)
                bad.Add($"{p.Name}: shows {Py.Quantity(p.CurrentStock)}, ledger {Py.Normalized(l.Qty)}");
        }
        return new IntegrityCheck("stock_quantity", "On-hand quantity = stock ledger", bad.Count == 0, 0, bad.Count,
            bad.Count > 0 ? string.Join("; ", bad.Take(5)) : $"{products.Count} products checked");
    }

    private static readonly string[] OutOfService = ["disposed", "transferred", "inactive", "draft", "cancelled"];

    public async Task<IntegrityCheck> FixedAssetsVsGlAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        var live = await db.Set<FixedAsset>().AsNoTracking()
            .Where(a => a.Status == null || !OutOfService.Contains(a.Status))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.PurchaseCost, a.SalvageValue })
            .ToListAsync(ct);
        // posted_depreciation: depreciation rows still backed by a posted journal (or none), floored at cost.
        var charged = (await db.Set<AssetDepreciation>().AsNoTracking()
                .Where(d => d.JournalEntryId == null || d.JournalEntry!.IsPosted == true)
                .GroupBy(d => d.AssetId).Select(g => new { g.Key, Total = g.Sum(x => x.Amount) }).ToListAsync(ct))
            .ToDictionary(r => r.Key, r => r.Total);
        decimal cost = 0m, dep = 0m;
        foreach (var a in live)
        {
            if (a.SalvageValue is null)
                throw new InvalidOperationException("unsupported operand type(s) for -: 'float' and 'NoneType'");
            cost += Py.FromDouble(Py.ToDouble(a.PurchaseCost));
            double depreciable = Py.ToDouble(a.PurchaseCost) - Py.ToDouble(a.SalvageValue.Value);
            dep += Py.FromDouble(Math.Min(Py.ToDouble(charged.GetValueOrDefault(a.Id)), depreciable));
        }
        decimal glCost = GlBalance(["1-02-01", "1-02-03"]);
        decimal glDep = -GlBalance(["1-02-02"]);
        bool ok = Math.Abs(cost - glCost) <= Tol && Math.Abs(dep - glDep) <= Tol;
        return new IntegrityCheck("fixed_assets_vs_gl", "Asset register = fixed-asset accounts", ok, cost - dep, glCost - glDep,
            $"Cost {Py.Comma2(cost)} vs GL {Py.Comma2(glCost)}; accumulated depreciation {Py.Comma2(dep)} vs GL {Py.Comma2(glDep)}");
    }

    private sealed record Doc(int Id, string? Status, string? Number, decimal? Value, bool HasItemValue, bool Posted, string? VoucherType);

    public async Task<IntegrityCheck> DocumentsPostedAsync(CancellationToken ct = default)
    {
        var je = db.Set<JournalEntry>().AsNoTracking();
        // (documents, approved status, journal voucher_type); Posted = a posted journal exists for (type, id).
        var groups = new List<(List<Doc> Docs, bool ItemsDecide)>
        {
            (await db.Set<InvInvoice>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.VoucherStatus, d.InvoiceNumber, d.TotalAmount, false,
                    je.Any(e => e.VoucherType == "SI" && e.VoucherId == d.Id && e.IsPosted == true), "SI")).ToListAsync(ct), false),
            (await db.Set<InvPurchaseInvoice>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.Status, d.InvoiceNumber, d.TotalAmount, false,
                    je.Any(e => e.VoucherType == "PI" && e.VoucherId == d.Id && e.IsPosted == true), "PI")).ToListAsync(ct), false),
            (await db.Set<InvSalesReturn>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.Status, d.ReturnNumber, d.NetReturnAmount, false,
                    je.Any(e => e.VoucherType == "SR" && e.VoucherId == d.Id && e.IsPosted == true), "SR")).ToListAsync(ct), false),
            (await db.Set<InvPurchaseReturn>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.Status, d.ReturnNumber, d.NetReturnAmount, false,
                    je.Any(e => e.VoucherType == "PR" && e.VoucherId == d.Id && e.IsPosted == true), "PR")).ToListAsync(ct), false),
            (await db.Set<ConsumptionVoucher>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.Status, d.VoucherNumber, null,
                    d.ConsumptionItems.Any(i => i.TotalCost > 0.005m || i.TotalCost < -0.005m),
                    je.Any(e => e.VoucherType == "CONS" && e.VoucherId == d.Id && e.IsPosted == true), "CONS")).ToListAsync(ct), true),
            (await db.Set<ScrapVoucher>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.Status, d.VoucherNumber, null,
                    d.ScrapItems.Any(i => i.TotalCost > 0.005m || i.TotalCost < -0.005m),
                    je.Any(e => e.VoucherType == "SCRAP" && e.VoucherId == d.Id && e.IsPosted == true), "SCRAP")).ToListAsync(ct), true),
            (await db.Set<AccountingVoucher>().AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new Doc(d.Id, d.Status, d.VoucherNumber, null, true,
                    je.Any(e => e.VoucherType == d.VoucherType && e.VoucherId == d.Id && e.IsPosted == true), d.VoucherType)).ToListAsync(ct), true),
        };
        var problems = new List<string>();
        int checkedCount = 0;
        foreach (var (docs, itemsDecide) in groups)
            foreach (var doc in docs)
            {
                checkedCount++;
                if (string.IsNullOrEmpty(doc.VoucherType)) continue;
                bool approved = doc.Status == "approved";
                // _has_value: the document's total when it has one, else its items' cost (vouchers), else yes.
                bool hasValue = itemsDecide ? doc.HasItemValue : doc.Value is decimal v && Math.Abs(v) > 0.005m;
                string number = doc.Number ?? "None";
                if (approved && !doc.Posted && hasValue) problems.Add($"{number} approved but not posted");
                if (doc.Posted && !approved) problems.Add($"{number} posted but not approved");
            }
        return new IntegrityCheck("documents_posted", "Approved documents = posted journals", problems.Count == 0, 0, problems.Count,
            problems.Count > 0 ? string.Join("; ", problems.Take(6)) : $"{checkedCount} documents checked");
    }

    /// <summary>Credit minus debit on one account, per (voucher type, voucher id), in posted journals of these types.</summary>
    private async Task<Dictionary<(string Type, int Id), decimal>> VoucherNetsAsync(int accountId, string[] types, CancellationToken ct) =>
        (await db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && l.JournalEntry.IsPosted == true && types.Contains(l.JournalEntry.VoucherType))
            .GroupBy(l => new { l.JournalEntry.VoucherType, l.JournalEntry.VoucherId })
            .Select(g => new { g.Key.VoucherType, g.Key.VoucherId, Net = g.Sum(x => x.Credit ?? 0) - g.Sum(x => x.Debit ?? 0) })
            .ToListAsync(ct))
        .ToDictionary(r => (r.VoucherType, r.VoucherId), r => r.Net);

    public async Task<IntegrityCheck> PayrollVsGlAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        var runs = await db.Set<PayrollRun>().AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Month, r.Year, r.TotalNet, r.PaidAt }).ToListAsync(ct);
        var payable = LegacyAccount("2121");
        var nets = payable is null ? [] : await VoucherNetsAsync(payable.Id, ["PRL", "PRL-PAY"], ct);
        var bad = new List<string>();
        foreach (var r in runs)
        {
            decimal net = r.TotalNet ?? 0m;
            decimal got = nets.GetValueOrDefault(("PRL", r.Id));
            if (Math.Abs(got - net) > Tol)
                bad.Add($"{r.Month}/{r.Year}: net {Py.Comma2(net)}, Salary Payable credited {Py.Comma2(got)}");
            if (r.PaidAt is not null)
            {
                decimal paid = -nets.GetValueOrDefault(("PRL-PAY", r.Id));
                if (Math.Abs(paid - net) > Tol)
                    bad.Add($"{r.Month}/{r.Year}: marked paid, payment journal {Py.Comma2(paid)}");
            }
        }
        return Check("payroll_vs_gl", "Payroll runs = salary journals", 0, bad.Count,
            bad.Count > 0 ? string.Join("; ", bad.Take(5)) : $"{runs.Count} runs checked", tol: 0);
    }

    public async Task<IntegrityCheck> StaffLoansVsGlAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        var je = db.Set<JournalEntry>().AsNoTracking();
        var loans = await db.Set<LoanAdvanceRequest>().AsNoTracking()
            .Where(l => je.Any(e => e.VoucherType == "LOAN-DSB" && e.IsPosted == true && e.VoucherId == l.Id))
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.RemainingAmount, l.Status }).ToListAsync(ct);
        var bad = new List<string>();
        foreach (var loan in loans)
        {
            string code = $"{LoanParentCode}-{loan.Id + EntityIdOffset:D4}";
            decimal bal = GlBalance([code]);
            decimal owed = loan.Status is "approved" or "paid" ? loan.RemainingAmount ?? 0m : 0m;
            if (Math.Abs(bal - owed) > Tol)
                bad.Add($"#{loan.Id}: still to recover {Py.Comma2(owed)}, account {Py.Comma2(bal)}");
        }
        return Check("staff_loans_vs_gl", "Staff loans = loan accounts", 0, bad.Count,
            bad.Count > 0 ? string.Join("; ", bad.Take(5)) : $"{loans.Count} loans paid out through payroll checked", tol: 0);
    }

    public async Task<IntegrityCheck> PfFundVsGlAsync(CancellationToken ct = default)
    {
        await EnsureLedgerAsync(ct);
        var fund = LegacyAccount("2123");
        var sums = (await db.Set<PfLedger>().AsNoTracking().GroupBy(_ => 1)
            .Select(g => new { Dr = g.Sum(x => x.Debit ?? 0), Cr = g.Sum(x => x.Credit ?? 0) }).ToListAsync(ct)).FirstOrDefault();
        decimal members = (sums?.Cr ?? 0m) - (sums?.Dr ?? 0m);
        decimal gl = fund is null ? 0m : -GlBalance([], [fund.Id]);
        return Check("pf_fund_vs_gl", "PF members' ledger = PF Payable", members, gl,
            $"Members' balances {Py.Comma2(members)} vs PF Payable {Py.Comma2(gl)}");
    }

    /// <summary>The most recent stock re-costings (the page's second table and the export's second sheet).</summary>
    public Task<List<StockCostAdjustment>> RecentAdjustmentsAsync(CancellationToken ct = default) =>
        db.Set<StockCostAdjustment>().AsNoTracking().OrderByDescending(a => a.Id).Take(25).ToListAsync(ct);
}
