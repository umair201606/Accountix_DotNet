using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Orders.Tests;

/// <summary>
/// A fresh company of owner_b's (company B's admin), made and provisioned through .NET the way the portal
/// makes one (<see cref="CompanySetup.ProvisionAsync"/>), with its own two customers, a supplier and three
/// products. Nothing here depends on what earlier runs left in the database. Most tests share one world per
/// run (<see cref="SharedAsync"/>); a test that reads whole lists makes its own (<see cref="CreateAsync"/>).
/// Golden/capture/_world.py is this file's Python twin: the same data, payloads and line formats.
/// </summary>
internal sealed partial class World
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static World? _shared;

    public string Tag { get; } = Guid.NewGuid().ToString("N")[..8];
    public int Company { get; private set; }
    public int Admin { get; } = Reference.UserId(Reference.Email("owner_b"));
    public string AdminName { get; private set; } = "";
    /// <summary>role (C1, C2, S1, P1..P3) → id.</summary>
    public Dictionary<string, int> Ids { get; } = new();
    public int Customer => Ids["C1"];
    public int Customer2 => Ids["C2"];
    public int Supplier => Ids["S1"];
    public int[] Products => [Ids["P1"], Ids["P2"], Ids["P3"]];

    /// <summary>Documents the test made: (SO|PO, id) → role, and their numbers → role.</summary>
    private readonly Dictionary<(string Kind, int Id), string> _docs = new();
    private readonly Dictionary<string, string> _numbers = new();

    public static async Task<World> SharedAsync()
    {
        await Gate.WaitAsync();
        try { return _shared ??= await CreateAsync(); }
        finally { Gate.Release(); }
    }

    public static async Task<World> CreateAsync()
    {
        var w = new World();
        var now = DateTime.UtcNow;
        await using (var db = Db(null))
        {
            w.AdminName = await db.Users.Where(u => u.Id == w.Admin).Select(u => u.FullName).SingleAsync();
            // The portal's create (shared/routes/portal.py create): the company, its admin, then provisioning.
            var company = new Company
            {
                Name = $"Orders Tests {w.Tag}", Slug = $"orders-tests-{w.Tag}", IsActive = true, PlanName = "free", CreatedBy = w.Admin,
                ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
                ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
                Country = "Pakistan", Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            w.Company = company.Id;
            int adminRole = await db.Roles.Where(r => r.Name == Accountix.Core.Entities.Role.Admin).Select(r => r.Id).SingleAsync();
            var m = new CompanyMembership { CompanyId = company.Id, UserId = w.Admin, RoleId = adminRole, Status = CompanyMembership.StatusActive, JoinedAt = now };
            m.SetModuleFlags(Modules.All.ToDictionary(x => x.Key, _ => true));
            db.Memberships.Add(m);
            await db.SaveChangesAsync();
            await new CompanySetup(db, new FirstUse(db, TimeProvider.System), TimeProvider.System).ProvisionAsync(company.Id);
        }
        await using (var db = Db(w.Company))
        {
            // The master-data forms' rows (model defaults included): fixed names, as the world is the company's own.
            var c1 = new InvCustomer { Name = "Solar City Traders", ContactPerson = "Imran", Phone = "042-1111111", Address = "1 Mall Road", City = "Lahore", TaxId = "NTN-1234567", CreditLimit = 0, IsActive = true, CreatedAt = now };
            var c2 = new InvCustomer { Name = "Green Homes", Address = "Plot 7, DHA", City = "Karachi", CreditLimit = 0, IsActive = true, CreatedAt = now };
            db.AddRange(c1, c2);
            await db.SaveChangesAsync();
            var s1 = new InvSupplier { Name = "Sun Power Imports", ContactPerson = "Aslam", Phone = "021-2222222", Address = "Ind. Area", City = "Karachi", TaxId = "NTN-7654321", IsActive = true, CreatedAt = now };
            db.Add(s1);
            await db.SaveChangesAsync();
            InvProduct P(string sku, string name, decimal price, decimal cost, int reorder, string hs, decimal weight) => new()
            {
                Sku = sku, Name = name, UnitPrice = price, CostPrice = cost, ReorderLevel = reorder, CurrentStock = 0, Unit = "pcs",
                HsCode = hs, Weight = weight, IsActive = true, CreatedAt = now, UpdatedAt = now,
            };
            var p1 = P("SOL-550", "Solar Panel 550W", 25000m, 18000m, 5, "8541.4000", 28.5m);
            var p2 = P("INV-6K", "Inverter 6kW", 145000m, 110000m, 2, "8504.4000", 12m);
            var p3 = P("BAT-200", "Battery 200Ah", 30750.17m, 24000m, 4, "8507.2000", 60m);
            foreach (var p in new[] { p1, p2, p3 })
            {
                db.Add(p);
                await db.SaveChangesAsync();         // one by one: ids in this order, as the Python twin flushes them
            }
            w.Ids["C1"] = c1.Id; w.Ids["C2"] = c2.Id; w.Ids["S1"] = s1.Id;
            w.Ids["P1"] = p1.Id; w.Ids["P2"] = p2.Id; w.Ids["P3"] = p3.Id;
        }
        return w;
    }

    public static AccountixDbContext Db(int? company)
    {
        var ctx = new CompanyContext();
        ctx.Set(company);
        return Reference.NewDb(ctx);
    }

    public AccountixDbContext Db() => Db(Company);

    public PageClient Client(ModuleHost host) => host.ClientFor(Admin, Company);

    public static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Forget the roles an earlier test gave its documents (a golden test on the shared world names its own).</summary>
    public World ClearRoles()
    {
        _docs.Clear();
        _numbers.Clear();
        return this;
    }

    /// <summary>Give a document the test made a role (SO1, PO2, ...) for the normalised dumps.</summary>
    public void Name(string kind, int id, string? number, string role)
    {
        _docs[(kind, id)] = role;
        if (!string.IsNullOrEmpty(number)) _numbers[number] = role;
    }

    /// <summary>Save through the page and name the document from its answer.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> SaveAsync(PageClient c, string url, JsonNode payload, string token,
                                                                                     string? role = null)
    {
        var r = await c.PostJsonWithTokenAsync(url, payload, token);
        string raw = await r.Content.ReadAsStringAsync();
        var body = Parse(raw);
        if (role is not null && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var id))
            Name(url.Contains("/purchases/") ? "PO" : "SO", id.GetInt32(), body.TryGetProperty("number", out var n) ? n.GetString() : null, role);
        return (r.StatusCode, body, raw);
    }

    private static JsonElement Parse(string text)
    {
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse(JsonSerializer.Serialize(new { raw = text[..Math.Min(400, text.Length)] })).RootElement.Clone(); }
    }

    // ── Payloads: what the editors' collectData() sends ─────────────────

    /// <summary>A sales order as the browser's collectData() sends it (ids as text, empty fields as 0).</summary>
    public JsonObject SalesJson(string action, string note, int? id = null, int? version = null, double qty = 2.5,
                                double price = 33333.333333333, string tax = "17") => new()
    {
        ["id"] = id is int i ? i.ToString(Inv) : null,
        ["action"] = action,
        ["so_number"] = id is null ? "(auto)" : "",
        ["customer_id"] = Customer.ToString(Inv),
        ["party_account_id"] = null,
        ["order_date"] = "2026-05-12",
        ["expected_date"] = "2026-05-30",
        ["discount_mode"] = "general", ["charges_mode"] = "general", ["tax_mode"] = "general",
        ["global_discount_pct"] = 0, ["global_discount_value"] = 0, ["global_delivery"] = 0, ["global_installation"] = 0,
        ["global_sales_tax_pct"] = tax, ["further_tax_pct"] = 0, ["apply_further_tax"] = false,
        ["withholding_tax_pct"] = 0, ["apply_withholding_tax"] = false,
        ["notes"] = note,
        ["subtotal"] = Math.Round(qty * price, 2), ["total_discount"] = 0, ["total_charges"] = 0,
        ["total_tax"] = 0.1 + 0.2, ["total_further_tax"] = 0, ["total_withholding_tax"] = 0,
        ["total_amount"] = Math.Round(qty * price, 2) + 0.1 + 0.2,
        ["items"] = new JsonArray(
            Line(Products[0], qty, price, "First line"),
            Line(Products[1], 1, 145000, "Second line")),
        ["charges"] = new JsonArray(),
        ["edit_version"] = version,
    };

    /// <summary>A purchase order as purchases/form_inv.html collectData() sends it.</summary>
    public JsonObject PurchaseJson(string action, string note, int? id = null, int? version = null) => new()
    {
        ["id"] = id is int i ? i.ToString(Inv) : null,
        ["action"] = action,
        ["po_number"] = id is null ? "(auto)" : "",
        ["supplier_id"] = Supplier.ToString(Inv),
        ["party_account_id"] = null,
        ["order_date"] = "2026-05-14",
        ["expected_date"] = 0,
        ["discount_mode"] = "general", ["charges_mode"] = "general", ["tax_mode"] = "individual",
        ["global_discount_pct"] = 0, ["global_discount_value"] = 0,
        ["global_sales_tax_pct"] = 0, ["withholding_tax_pct"] = 0, ["apply_withholding_tax"] = false,
        ["notes"] = note,
        ["driver_name"] = "Aslam", ["driver_contact"] = 0, ["vehicle_number"] = "LEA-1234", ["gate_pass"] = 0,
        ["subtotal"] = 92250.5, ["total_discount"] = 0, ["total_charges"] = 0, ["total_tax"] = 15682.585,
        ["total_withholding_tax"] = 0, ["total_amount"] = 107933.085,
        ["items"] = new JsonArray(
            Line(Products[2], 3, 30750.1666666, "Panels", tax: 17),
            Line(Products[0], 0.25, 1, "Cable", tax: 0)),
        ["charges"] = new JsonArray(),
        ["edit_version"] = version,
    };

    private static JsonObject Line(int product, double qty, double price, string desc, double tax = 0) => new()
    {
        ["product_id"] = product, ["description"] = desc, ["quantity"] = qty, ["unit"] = "pcs", ["unit_price"] = price,
        ["discount_pct"] = 0, ["discount_amount"] = 0, ["sales_tax_pct"] = tax,
        ["total_before_discount"] = Math.Round(qty * price, 2), ["total_after_discount"] = 0,
        ["total_price"] = Math.Round(qty * price, 2),
    };

    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(PageClient c, string url, JsonNode payload, string token)
    {
        var r = await c.PostJsonWithTokenAsync(url, payload, token);
        return (r.StatusCode, Parse(await r.Content.ReadAsStringAsync()));
    }

    // ── Normalisation (the same in _world.py) ──────────────────────────

    /// <summary>id → role for one kind of row ("C" customers, "S" suppliers, "P" products): ids of two tables can be equal.</summary>
    public Dictionary<int, string> Of(string kind) => Ids.Where(kv => kv.Key.StartsWith(kind, StringComparison.Ordinal)).ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Document id → role. Sales and purchase orders are numbered separately, so on a fresh database SO 1 and PO 1
    /// both exist: pass the kind ("SO" / "PO") wherever the page or answer says which; with no kind, an id two kinds
    /// share reads as both roles.
    /// </summary>
    private Dictionary<int, string> DocIds(string? kind = null) => _docs
        .Where(kv => kind is null || kv.Key.Kind == kind)
        .GroupBy(kv => kv.Key.Id)
        .ToDictionary(g => g.Key, g => string.Join("/", g.Select(kv => kv.Value)));

    /// <summary>The payload with party / product / document ids written as their roles (the fixture's inputs).</summary>
    public JsonNode RoleJson(JsonNode payload)
    {
        var kinds = new Dictionary<string, Dictionary<int, string>> { ["customer_id"] = Of("C"), ["supplier_id"] = Of("S"), ["product_id"] = Of("P") };
        var docs = DocIds(payload is JsonObject top && top.ContainsKey("supplier_id") ? "PO"
                          : payload is JsonObject top2 && top2.ContainsKey("customer_id") ? "SO" : null);
        JsonNode? Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    var copy = new JsonObject();
                    foreach (var (k, v) in o)
                    {
                        string? s = v is JsonValue jv ? jv.ToJsonString().Trim('"') : null;
                        if (kinds.TryGetValue(k, out var map) && s is { Length: > 0 } && s != "0" && s != "null")
                            copy[k] = map.GetValueOrDefault(int.Parse(s, Inv), "?");
                        else if (k == "id" && s is { Length: > 0 } && s != "null")
                            copy[k] = docs.GetValueOrDefault(int.Parse(s, Inv), "?");
                        else
                            copy[k] = Walk(v);
                    }
                    return copy;
                case JsonArray a:
                    return new JsonArray(a.Select(Walk).ToArray());
                default:
                    return n?.DeepClone();
            }
        }
        return Walk(payload)!;
    }

    /// <summary>Numbers, the tag and the admin's name of this world written as roles.</summary>
    public string Text(string? s)
    {
        s ??= "";
        foreach (var (n, role) in _numbers.OrderByDescending(kv => kv.Key.Length))
            s = s.Replace(n, $"<{role}>");
        s = s.Replace(Tag, "TAG");
        if (AdminName.Length > 0) s = s.Replace(AdminName, "<admin>");
        return s;
    }

    /// <summary>A JSON answer: status, then the body canonical (keys sorted, ids and numbers as roles, who/when dropped).</summary>
    public string Answer(HttpStatusCode status, string raw)
    {
        JsonElement body;
        try { body = JsonDocument.Parse(raw).RootElement.Clone(); }
        catch (JsonException) { return $"{(int)status} null"; }
        var docs = DocIds();
        string text;
        if (body.ValueKind == JsonValueKind.Object)
            text = "{" + string.Join(",", body.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p =>
            {
                string v = p.Name == "id" && p.Value.ValueKind == JsonValueKind.Number ? Quote(docs.GetValueOrDefault(p.Value.GetInt32(), "?"))
                         : p.Name == "error" && p.Value.ValueKind == JsonValueKind.String ? Quote(WhoWhen().Replace(p.Value.GetString()!, ""))
                         : Canonical(p.Value);
                return Quote(p.Name) + ":" + v;
            })) + "}";
        else
            text = Canonical(body);
        return $"{(int)status} {Text(text)}";
    }

    [GeneratedRegex(@" by .+? at \d\d:\d\d on \d\d \w{3}")]
    private static partial Regex WhoWhen();

    public async Task<List<string>> SalesRowsAsync(int id)
    {
        await using var db = Db();
        var o = await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(x => x.Id == id);
        var items = await db.Set<InvSalesOrderItem>().AsNoTracking().Where(i => i.SoId == id).OrderBy(i => i.Id).ToListAsync();
        var c = Of("C");
        var p = Of("P");
        bool numberOk = o.SoNumber.Length > 0 && o.SoNumber == $"SO-{o.SoNumber[3..9]}-{id:D4}";
        var outL = new List<string>
        {
            "SO " + string.Join("|", _docs.GetValueOrDefault(("SO", id), "?"), Co(o.CompanyId), Role(c, o.CustomerId), V(o.PartyAccountId),
                V(o.OrderDate), V(o.ExpectedDate), V(o.Status), V(o.FulfilmentStatus), V(o.TaxMode), V(o.GlobalSalesTaxPct), V(o.Subtotal),
                V(o.TotalTax), V(o.TotalAmount), Text(V(o.Notes)), User(o.CreatedBy), User(o.ApprovedBy), V(o.ApprovedAt is not null),
                V(o.EditVersion), V(o.CreatedAt is not null), V(numberOk)),
        };
        foreach (var i in items)
            outL.Add("  item " + string.Join("|", Co(i.CompanyId), Role(p, i.ProductId), Text(V(i.Description)), Text(V(i.Unit)), V(i.Quantity),
                V(i.InvoicedQty), V(i.UnitPrice), V(i.SalesTaxPct), V(i.TotalBeforeDiscount), V(i.TotalAfterDiscount), V(i.TotalPrice)));
        return outL;
    }

    public async Task<List<string>> PurchaseRowsAsync(int id)
    {
        await using var db = Db();
        var o = await db.Set<InvPurchaseOrder>().AsNoTracking().SingleAsync(x => x.Id == id);
        var items = await db.Set<InvPurchaseOrderItem>().AsNoTracking().Where(i => i.PoId == id).OrderBy(i => i.Id).ToListAsync();
        var s = Of("S");
        var p = Of("P");
        bool numberOk = o.PoNumber == $"PO-{o.PoNumber[3..9]}-{id:D4}";
        var outL = new List<string>
        {
            "PO " + string.Join("|", _docs.GetValueOrDefault(("PO", id), "?"), Co(o.CompanyId), Role(s, o.SupplierId), V(o.PartyAccountId),
                V(o.OrderDate), V(o.ExpectedDate), V(o.Status), V(o.FulfilmentStatus), V(o.TaxMode), V(o.GlobalSalesTaxPct), V(o.Subtotal),
                V(o.TotalTax), V(o.TotalAmount), Text(V(o.Notes)), V(o.DriverName), V(o.DriverContact), V(o.VehicleNumber), V(o.GatePass),
                User(o.CreatedBy), User(o.ApprovedBy), V(o.ApprovedAt is not null), V(o.EditVersion), V(o.CreatedAt is not null), V(numberOk)),
        };
        foreach (var i in items)
            outL.Add("  item " + string.Join("|", Co(i.CompanyId), Role(p, i.ProductId), Text(V(i.Description)), Text(V(i.Unit)), V(i.Quantity),
                V(i.InvoicedQty), V(i.UnitPrice), V(i.SalesTaxPct), V(i.TotalBeforeDiscount), V(i.TotalAfterDiscount), V(i.TotalPrice)));
        return outL;
    }

    private string Co(int? id) => id == Company ? "CO" : "?";
    private static string Role(Dictionary<int, string> map, int? id) => id is int i ? map.GetValueOrDefault(i, "?") : "?";
    private string User(int? id) => id is null ? "null" : id == Admin ? "U" : "?";

    /// <summary>A column value: null, True/False, dates ISO, numbers without trailing zeros.</summary>
    public static string V(object? x) => x switch
    {
        null => "null",
        bool b => b ? "True" : "False",
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        DateTime t => t.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
        decimal m => Num(m),
        int i => i.ToString(Inv),
        long l => l.ToString(Inv),
        _ => x.ToString() ?? "",
    };

    public static string Num(decimal d) => d == 0 ? "0" : d.ToString("0.############################", Inv);

    public static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

    /// <summary>JSON with keys sorted, numbers as plain decimals, strings quoted without any other escaping.</summary>
    public static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                                     .Select(p => Quote(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.Number => Num(decimal.Parse(e.GetRawText(), NumberStyles.Float, Inv)),
        JsonValueKind.String => Quote(e.GetString()!),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null",
    };

    // ── Pages ──────────────────────────────────────────────────────────

    /// <summary>The company's today (Asia/Karachi), as the new editors default their dates to it.</summary>
    public static string Today() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")).ToString("yyyy-MM-dd", Inv);

    /// <summary>An editor's fingerprint (OrderPageTests.Fingerprint) with this world's ids, numbers and times as roles.</summary>
    public List<string> EditorLines(IEnumerable<string> fingerprint, string? kind = null)
    {
        var docs = DocIds(kind);
        var c = Of("C");
        var s = Of("S");
        var p = Of("P");
        string today = Today();
        string Id(Dictionary<int, string> map, string v) => int.TryParse(v, NumberStyles.None, Inv, out int i) ? map.GetValueOrDefault(i, "?") : v;
        return fingerprint.Select(line =>
        {
            line = Regex.Replace(line, @"^(input#orderId value=)(\d+)", m => m.Groups[1].Value + Id(docs, m.Groups[2].Value));
            line = Regex.Replace(line, @"^(input#customerId value=)(\d+)", m => m.Groups[1].Value + Id(c, m.Groups[2].Value));
            line = Regex.Replace(line, @"^(input#supplierId value=)(\d+)", m => m.Groups[1].Value + Id(s, m.Groups[2].Value));
            line = Regex.Replace(line, "\"(id|product_id)\": (\\d+)", m => $"\"{m.Groups[1].Value}\": \"{Id(p, m.Groups[2].Value)}\"");
            line = Regex.Replace(line, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", "<at>");
            line = line.Replace("value=" + today + " ", "value=<today> ");
            return Text(line);
        }).ToList();
    }

    /// <summary>The script after its data lines, as a SHA-256 (the template's code to the byte; the data lines are in the fingerprint).</summary>
    public static string ScriptBody(string script)
    {
        int at = script.IndexOf("\n\n", StringComparison.Ordinal);
        string body = at < 0 ? script : script[at..];
        return "script-body sha256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>A list page's filter and table (OrderPageTests.ListBody), one line per table row, ids and numbers as roles.</summary>
    public List<string> ListLines(string body)
    {
        var so = DocIds("SO");
        var po = DocIds("PO");
        body = Regex.Replace(body, @"(/inventory/(sales|purchases)/(?:cancel/|delete/)?)(\d+)",
            m => m.Groups[1].Value + (int.TryParse(m.Groups[3].Value, NumberStyles.None, Inv, out int i)
                ? (m.Groups[2].Value == "sales" ? so : po).GetValueOrDefault(i, "?") : "?"));
        return Text(body).Split("<tr>").Select((part, k) => k == 0 ? part : "<tr>" + part).ToList();
    }

    /// <summary>A lookup's JSON, canonical, with ids written by what they are (product, party, document, line, account).</summary>
    public string LookupJson(int status, string raw, string partyKind)
    {
        JsonElement body;
        try { body = JsonDocument.Parse(raw).RootElement.Clone(); }
        catch (JsonException) { return $"{status} null"; }
        var docs = DocIds(partyKind == "S" ? "PO" : "SO");
        var p = Of("P");
        var party = Of(partyKind);
        string Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) return "[" + string.Join(",", e.EnumerateArray().Select(Walk)) + "]";
            if (e.ValueKind != JsonValueKind.Object) return Canonical(e);
            bool product = e.TryGetProperty("sku", out _), account = e.TryGetProperty("code", out _),
                 doc = e.TryGetProperty("so_number", out _) || e.TryGetProperty("po_number", out _), line = e.TryGetProperty("ordered_qty", out _);
            return "{" + string.Join(",", e.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x =>
            {
                string v;
                if (x.Value.ValueKind == JsonValueKind.Number && x.Name == "product_id") v = Quote(p.GetValueOrDefault(x.Value.GetInt32(), "?"));
                else if (x.Value.ValueKind == JsonValueKind.Number && x.Name == "id")
                    v = Quote(product ? p.GetValueOrDefault(x.Value.GetInt32(), "?") : account ? "A" : doc ? docs.GetValueOrDefault(x.Value.GetInt32(), "?")
                            : line ? "line" : party.GetValueOrDefault(x.Value.GetInt32(), "?"));
                else v = Walk(x.Value);
                return Quote(x.Name) + ":" + v;
            })) + "}";
        }
        return $"{status} {Text(Walk(body))}";
    }
}
