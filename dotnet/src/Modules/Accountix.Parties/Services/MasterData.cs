using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;

namespace Accountix.Parties.Services;

/// <summary>A master-data value people must correct (shared/master_data.Invalid).</summary>
public sealed class MasterDataInvalid(string message) : Exception(message);

/// <summary>Customer or supplier: the two differ only in names and the credit limit.</summary>
public sealed record PartyKind(string Key, string Title, string Plural, string Prefix, string Ledger, bool CreditLimit)
{
    public static readonly PartyKind Customer = new("customer", "Customer", "customers", "/inventory/customers", "Trade Debtors", true);
    public static readonly PartyKind Supplier = new("supplier", "Supplier", "suppliers", "/inventory/suppliers", "Trade Creditors", false);

    public string TitlePlural => Title + "s";
    public IReadOnlyList<string> Columns => CreditLimit ? MasterData.CustomerColumns : MasterData.SupplierColumns;
}

/// <summary>One cleaned customer / supplier (clean_party's values), plus the review screen's row facts.</summary>
public sealed class PartyValues
{
    public Dictionary<string, string> Text { get; } = new();
    /// <summary>The credit limit when it read as a number (customers).</summary>
    public decimal? CreditLimit { get; set; }
    /// <summary>What was typed when it did not (shown back as typed).</summary>
    public string? CreditLimitText { get; set; }
    public int? RowLine { get; set; }
    public List<string> RowErrors { get; set; } = [];

    public string this[string field] => Text.GetValueOrDefault(field, "");

    /// <summary>The review screen's credit-limit cell: a whole number without ".0", else Python's float text.</summary>
    public string CreditLimitShown => CreditLimit is decimal d
        ? d == Math.Truncate(d) ? Math.Truncate(d).ToString(CultureInfo.InvariantCulture) : TrackingRules.PyFloat(d)
        : CreditLimitText ?? "";
}

/// <summary>
/// The rules the customer and supplier forms, Excel imports and batch saves share (shared/master_data.py):
/// one set of checks, the same words everywhere.
/// </summary>
public static class MasterData
{
    public const int MaxListed = 10;
    public const int MaxImportRows = 2000;
    public static readonly decimal MaxNumber = 1_000_000_000_000m;

    public static readonly string[] PartyText =
        ["name", "contact_person", "email", "phone", "mobile", "address", "city", "tax_id", "payment_terms", "website", "notes"];

    public static readonly (string Field, string Label)[] PartyLabels =
    [
        ("name", "Name"), ("contact_person", "Contact person"), ("email", "Email"), ("phone", "Phone"), ("mobile", "Mobile"),
        ("city", "City"), ("tax_id", "Tax ID"), ("payment_terms", "Payment terms"), ("website", "Website"),
    ];

    public static readonly string[] CustomerColumns =
        ["name", "contact_person", "email", "phone", "mobile", "city", "tax_id", "payment_terms", "credit_limit", "website", "address", "notes"];
    public static readonly string[] SupplierColumns =
        ["name", "contact_person", "email", "phone", "mobile", "city", "tax_id", "payment_terms", "website", "address", "notes"];

    /// <summary>text(): any cell / form / JSON value as trimmed text ("" for null and booleans; 3001234567.0 → "3001234567").</summary>
    public static string Text(object? value) => (value switch
    {
        null or bool => "",
        double f when double.IsFinite(f) && f == Math.Floor(f) && Math.Abs(f) < 1e17 => ((long)f).ToString(CultureInfo.InvariantCulture),
        double f => TrackingRulesPy.Str(f),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        JsonElement e => Text(FromJson(e)),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    }).Trim();

    /// <summary>single_line: text with runs of whitespace collapsed.</summary>
    public static string SingleLine(object? value) =>
        string.Join(' ', Text(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>key(): the comparison form of a name.</summary>
    public static string Key(object? value) => SingleLine(value).ToLowerInvariant();

    /// <summary>number(): a non-negative amount from a form, cell or JSON value; MasterDataInvalid with a readable message.</summary>
    public static decimal Number(object? value, string label, decimal? minimum = 0m, bool whole = false, decimal fallback = 0m)
    {
        string raw = Text(value).Replace(",", "");
        if (raw.Length == 0) return fallback;
        string lower = raw.ToLowerInvariant().TrimStart('+', '-');
        if (lower is "inf" or "infinity" or "nan" or "snan")
            throw new MasterDataInvalid($"{label} must be a number.");
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n))
        {
            // Python's Decimal reads huge exponents a C# decimal cannot hold: still a number, just too large.
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double big) && !double.IsNaN(big))
                throw new MasterDataInvalid($"{label} is too large.");
            string shown = Text(value);
            throw new MasterDataInvalid($"{label} must be a number (got '{(shown.Length > 20 ? shown[..20] : shown)}').");
        }
        if (Math.Abs(n) > MaxNumber)
            throw new MasterDataInvalid($"{label} is too large.");
        if (minimum is decimal min && n < min)
            throw new MasterDataInvalid(min == 0 ? $"{label} cannot be negative." : $"{label} must be at least {min}.");
        if (whole && n != Math.Truncate(n))
            throw new MasterDataInvalid($"{label} must be a whole number.");
        return n;
    }

    /// <summary>summarise: the first messages joined, then "and N more".</summary>
    public static string Summarise(IEnumerable<string> messages, int limit = MaxListed)
    {
        var list = messages.ToList();
        string shown = string.Join("; ", list.Take(limit));
        if (list.Count > limit) shown += $"; and {list.Count - limit} more";
        return shown;
    }

    private static int Chars(string s) => s.EnumerateRunes().Count();

    /// <summary>
    /// clean_party: (values, errors) for a customer or supplier from any source. Names are unique per company
    /// ignoring case and spacing; text longer than its column is refused.
    /// </summary>
    public static async Task<(PartyValues Values, List<string> Errors)> CleanPartyAsync(
        AccountixDbContext db, PartyKind kind, IReadOnlyDictionary<string, object?> raw, int? excludeId = null, CancellationToken ct = default)
    {
        var values = new PartyValues();
        foreach (string f in PartyText)
            values.Text[f] = Text(raw.GetValueOrDefault(f));
        values.Text["name"] = SingleLine(values.Text["name"]);
        var errors = new List<string>();
        string name = values.Text["name"];
        if (name.Length == 0)
            errors.Add("Name is required.");
        else if (await TakenAsync(db, kind, name, excludeId, ct))
            errors.Add($"'{name}' already exists.");
        string email = values.Text["email"];
        if (email.Length > 0 && (!email.Contains('@') || !email[(email.LastIndexOf('@') + 1)..].Contains('.')))
            errors.Add($"Email '{(email.Length > 40 ? email[..40] : email)}' does not look like an email address.");
        if (kind.CreditLimit)
        {
            try
            {
                values.CreditLimit = Number(raw.GetValueOrDefault("credit_limit"), "Credit limit");
            }
            catch (MasterDataInvalid e)
            {
                errors.Add(e.Message);
                values.CreditLimitText = Text(raw.GetValueOrDefault("credit_limit"));
            }
        }
        var entity = db.Model.FindEntityType(kind.CreditLimit ? typeof(InvCustomer) : typeof(InvSupplier))!;
        foreach (var (field, label) in PartyLabels)
        {
            string prop = string.Concat(field.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            int? limit = entity.FindProperty(prop)?.GetMaxLength();
            string v = values.Text[field];
            if (limit is int max && Chars(v) > max)
                errors.Add($"{label} is longer than {max} characters.");
        }
        return (values, errors);
    }

    /// <summary>taken(): another row of this company already has this name (case and surrounding spaces ignored).</summary>
    public static Task<bool> TakenAsync(AccountixDbContext db, PartyKind kind, string value, int? excludeId, CancellationToken ct = default)
    {
        string wanted = SingleLine(value).ToLower(CultureInfo.InvariantCulture);
        int ex = excludeId ?? 0;
        return kind.CreditLimit
            ? db.Set<InvCustomer>().AsNoTracking().AnyAsync(c => c.Name.Trim().ToLower() == wanted && (ex == 0 || c.Id != ex), ct)
            : db.Set<InvSupplier>().AsNoTracking().AnyAsync(s => s.Name.Trim().ToLower() == wanted && (ex == 0 || s.Id != ex), ct);
    }

    /// <summary>read_sheet: the first (active) sheet's rows below the heading, with their spreadsheet row numbers.</summary>
    public static List<(int Line, object?[] Cells)> ReadSheet(Stream file)
    {
        XLWorkbook wb;
        try
        {
            var ms = new MemoryStream();
            file.CopyTo(ms);
            ms.Position = 0;
            wb = new XLWorkbook(ms);
        }
        catch (Exception)
        {
            throw new MasterDataInvalid("That file could not be read as an Excel workbook (.xlsx). Download the template and fill it in.");
        }
        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.TabActive) ?? wb.Worksheets.FirstOrDefault()
                     ?? throw new MasterDataInvalid("The workbook has no sheet to read.");
            var used = ws.RangeUsed();
            var output = new List<(int, object?[])>();
            if (used is null) return output;
            int lastRow = used.LastRow().RowNumber(), lastCol = used.LastColumn().ColumnNumber();
            for (int r = 2; r <= lastRow; r++)
            {
                var cells = new object?[lastCol];
                for (int c = 1; c <= lastCol; c++)
                    cells[c - 1] = CellValue(ws.Cell(r, c));
                if (cells.Any(v => Text(v).Length > 0))
                    output.Add((r, cells));
            }
            return output;
        }
    }

    private static object? CellValue(IXLCell cell)
    {
        var v = cell.CachedValue;
        return v.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Boolean => v.GetBoolean(),
            XLDataType.Number => v.GetNumber(),
            XLDataType.Text => v.GetText(),
            XLDataType.DateTime => v.GetDateTime(),
            XLDataType.TimeSpan => v.GetTimeSpan().ToString(),
            _ => v.ToString(),
        };
    }

    private static List<string> Repeats(PartyValues values, HashSet<string> seen, string where)
    {
        var output = new List<string>();
        string k = Key(values["name"]);
        if (k.Length == 0) return output;
        if (!seen.Add(k))
            output.Add($"'{SingleLine(values["name"])}' appears more than once {where}.");
        return output;
    }

    /// <summary>import_rows: every sheet row cleaned and kept, flagged with why it cannot be saved yet.</summary>
    public static async Task<List<PartyValues>> ImportRowsAsync(AccountixDbContext db, PartyKind kind,
        List<(int Line, object?[] Cells)> sheetRows, CancellationToken ct = default)
    {
        if (sheetRows.Count > MaxImportRows)
            throw new MasterDataInvalid($"The sheet has {sheetRows.Count.ToString("N0", CultureInfo.InvariantCulture)} rows; import at most {MaxImportRows.ToString("N0", CultureInfo.InvariantCulture)} at a time.");
        var seen = new HashSet<string>();
        var output = new List<PartyValues>();
        foreach (var (line, row) in sheetRows)
        {
            var raw = new Dictionary<string, object?>();
            for (int i = 0; i < kind.Columns.Count; i++)
                raw[kind.Columns[i]] = i < row.Length ? row[i] : null;
            var (values, errors) = await CleanPartyAsync(db, kind, raw, ct: ct);
            errors.AddRange(Repeats(values, seen, "in this sheet"));
            values.RowLine = line;
            values.RowErrors = errors;
            output.Add(values);
        }
        return output;
    }

    /// <summary>submitted_rows: the review screen's rows (objects, not deleted, not blank).</summary>
    public static List<Dictionary<string, object?>> SubmittedRows(string? rawJson)
    {
        const string unreadable = "The rows sent were not readable — reload the import and try again.";
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(rawJson) ? "[]" : rawJson);
            data = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new MasterDataInvalid(unreadable);
        }
        if (data.ValueKind != JsonValueKind.Array)
            throw new MasterDataInvalid(unreadable);
        if (data.GetArrayLength() > MaxImportRows)
            throw new MasterDataInvalid($"At most {MaxImportRows.ToString("N0", CultureInfo.InvariantCulture)} rows can be saved at a time.");
        var output = new List<Dictionary<string, object?>>();
        foreach (var r in data.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var row = r.EnumerateObject().GroupBy(p => p.Name).ToDictionary(g => g.Key, g => FromJson(g.Last().Value));
            if (Truthy(row.GetValueOrDefault("_delete"))) continue;
            if (!row.Any(kv => !kv.Key.StartsWith('_') && Text(kv.Value).Length > 0)) continue;
            output.Add(row);
        }
        return output;
    }

    /// <summary>A JSON value as Python's json module gives it (int / float / str / bool / None / raw for lists).</summary>
    public static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };

    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>save_rows: validate every submitted row again and create the good ones; the rest come back flagged.</summary>
    public static async Task<(int Created, List<PartyValues> Failed)> SaveRowsAsync(AccountixDbContext db, PartyKind kind,
        List<Dictionary<string, object?>> rows, Func<PartyValues, Task> create, CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        int created = 0;
        var failed = new List<PartyValues>();
        foreach (var raw in rows)
        {
            var (values, errors) = await CleanPartyAsync(db, kind, raw, ct: ct);
            errors.AddRange(Repeats(values, seen, "in these rows"));
            if (errors.Count > 0)
            {
                values.RowErrors = errors;
                failed.Add(values);
                continue;
            }
            await create(values);
            created++;
        }
        return (created, failed);
    }
}
