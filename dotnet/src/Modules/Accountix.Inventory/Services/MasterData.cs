using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accountix.Inventory.Services;

/// <summary>A value a master-data record cannot take (master_data.Invalid); the message is for people.</summary>
public sealed class InvalidValueException(string message) : BusinessRuleException(message);

/// <summary>
/// One record's values as shared/master_data.py carries them: a dict of field →
/// value, where a value keeps Python's type (string, double = float, decimal =
/// Decimal, int, bool, null) so the review screen prints what Python prints.
/// "row_line" / "row_errors" ride along for the import review.
/// </summary>
public sealed class Row : Dictionary<string, object?>
{
    public Row() { }
    public Row(IDictionary<string, object?> d) : base(d) { }

    public object? Get(string key) => TryGetValue(key, out var v) ? v : null;

    public List<string> Errors => TryGetValue("row_errors", out var v) && v is List<string> l ? l : [];

    /// <summary>Jinja's {{ r.key }}: a missing key prints nothing, None prints "None".</summary>
    public string Cell(string key) => TryGetValue(key, out var v) ? MasterData.Str(v) : "";
}

/// <summary>
/// Checks shared by the product and unit forms and their Excel imports
/// (shared/master_data.py): a form, an upload and a batch save refuse the same
/// things in the same words.
/// </summary>
public static partial class MasterData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Warnings listed in a flash message.</summary>
    public const int MaxListed = 10;

    /// <summary>A sheet longer than this is refused rather than half-read.</summary>
    public const int MaxImportRows = 2000;

    /// <summary>Larger than any amount the NUMERIC(20,6) columns hold sensibly.</summary>
    public static readonly decimal MaxNumber = 1_000_000_000_000m;

    public static readonly string[] ProductColumns =
        ["sku", "name", "description", "category_name", "unit_price", "cost_price", "reorder_level", "current_stock", "unit", "hs_code"];

    public static readonly string[] UnitColumns = ["name", "abbreviation", "explanation"];

    /// <summary>Column lengths of inv_products / inv_units (check_length).</summary>
    private static readonly (string Field, string Label, int Limit)[] ProductLengths =
        [("sku", "SKU", 50), ("name", "Name", 200), ("unit", "Unit", 20), ("hs_code", "HS code", 50)];

    private static readonly (string Field, string Label, int Limit)[] UnitLengths =
        [("name", "Name", 100), ("abbreviation", "Abbreviation", 20)];

    // ── Python's text forms ────────────────────────────────────────────────

    /// <summary>str(x) as Python prints it (float 100.0 → "100.0", None → "None").</summary>
    public static string Str(object? v) => v switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => Float(d),
        float f => Float(f),
        decimal m => m.ToString(Inv),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    /// <summary>repr(float): shortest round-trip, "100.0" for whole numbers, "1e-05" style exponents.</summary>
    public static string Float(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e16)
            return ((decimal)d).ToString("0", Inv) + ".0";
        string r = d.ToString("R", Inv);
        int e = r.IndexOfAny(['E', 'e']);
        if (e < 0) return r;
        string mantissa = r[..e];
        string exp = r[(e + 1)..];
        char sign = exp[0] == '-' ? '-' : '+';
        string digits = exp.TrimStart('+', '-').TrimStart('0');
        if (digits.Length < 2) digits = digits.PadLeft(2, '0');
        return mantissa + "e" + sign + digits;
    }

    /// <summary>A Python-float value as the decimal the database gets (psycopg2 sends repr()).</summary>
    public static decimal ToDecimal(double d) => decimal.Parse(Float(d), NumberStyles.Float, Inv);

    /// <summary>Python truthiness of a JSON / dict value.</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        double d => d != 0,
        decimal m => m != 0,
        int i => i != 0,
        long l => l != 0,
        JsonElement j => j.ValueKind switch
        {
            JsonValueKind.Array => j.GetArrayLength() > 0,
            JsonValueKind.Object => j.EnumerateObject().Any(),
            _ => true,
        },
        _ => true,
    };

    /// <summary>text: any cell / form / JSON value as trimmed text ('' for None and booleans; 3001234567.0 → "3001234567").</summary>
    public static string Text(object? value)
    {
        if (value is null or bool)
            return "";
        if (value is double d && !double.IsInfinity(d) && d == Math.Floor(d))
            return Math.Abs(d) < 7.9e27 ? ((decimal)d).ToString("0", Inv) : d.ToString("R", Inv);
        if (value is JsonElement j)
            return j.GetRawText().Trim();
        return Str(value).Trim();
    }

    /// <summary>single_line: text with runs of whitespace collapsed — names and codes.</summary>
    public static string SingleLine(object? value) =>
        string.Join(' ', Text(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>key: comparison form of a name (case and spacing do not make it new).</summary>
    public static string Key(object? value) => SingleLine(value).ToLowerInvariant();

    private static string Cut(string s, int n)
    {
        var runes = s.EnumerateRunes().ToList();
        return runes.Count <= n ? s : string.Concat(runes.Take(n).Select(r => r.ToString()));
    }

    [GeneratedRegex(@"^[+-]?(inf|infinity|nan|snan)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotFinite();

    [GeneratedRegex(@"^[+-]?(\d+(_\d+)*(\.(\d+(_\d+)*)?)?|\.\d+(_\d+)*)([eE][+-]?\d+(_\d+)*)?$")]
    private static partial Regex DecimalLiteral();

    /// <summary>
    /// number: a non-negative amount or quantity. Thousands separators allowed ("1,250.50"),
    /// blank gives <paramref name="default"/>, anything else unreadable is refused in words.
    /// </summary>
    public static decimal Number(object? value, string label, decimal? minimum = 0m, bool whole = false, decimal @default = 0m)
    {
        string raw = Text(value).Replace(",", "");
        if (raw.Length == 0)
            return @default;
        if (NotFinite().IsMatch(raw))
            throw new InvalidValueException($"{label} must be a number.");
        if (!DecimalLiteral().IsMatch(raw))
            throw new InvalidValueException($"{label} must be a number (got '{Cut(Text(value), 20)}').");
        decimal n;
        try
        {
            n = decimal.Parse(raw.Replace("_", ""), NumberStyles.Float, Inv);
        }
        catch (OverflowException)
        {
            throw new InvalidValueException($"{label} is too large.");
        }
        if (Math.Abs(n) > MaxNumber)
            throw new InvalidValueException($"{label} is too large.");
        if (minimum is decimal min && n < min)
            throw new InvalidValueException(min == 0 ? $"{label} cannot be negative." : $"{label} must be at least {min.ToString(Inv)}.");
        if (whole && n != decimal.Truncate(n))
            throw new InvalidValueException($"{label} must be a whole number.");
        return n;
    }

    private static IEnumerable<string> CheckLength(Row values, (string Field, string Label, int Limit)[] columns)
    {
        foreach (var (field, label, limit) in columns)
            if (values.Get(field) is string s && s.EnumerateRunes().Count() > limit)
                yield return $"{label} is longer than {limit} characters.";
    }

    /// <summary>taken: another row of this company already uses the value, ignoring case and surrounding spaces.</summary>
    public static Task<bool> TakenAsync<T>(IQueryable<T> rows, string property, string value, int? excludeId,
                                           CancellationToken ct = default) where T : class
    {
        string wanted = SingleLine(value).ToLower(Inv);
        var q = rows.Where(e => EF.Property<string>(e, property).Trim().ToLower() == wanted);
        if (excludeId is int x && x != 0)
            q = q.Where(e => EF.Property<int>(e, "Id") != x);
        return q.AnyAsync(ct);
    }

    /// <summary>summarise: the first <paramref name="limit"/> messages, then "and N more".</summary>
    public static string Summarise(IEnumerable<string> messages, int limit = MaxListed)
    {
        var list = messages.ToList();
        string shown = string.Join("; ", list.Take(limit));
        if (list.Count > limit)
            shown += $"; and {list.Count - limit} more";
        return shown;
    }

    // ── Sheets ────────────────────────────────────────────────────────────

    /// <summary>
    /// read_sheet: the value rows (header skipped) of the workbook's active sheet with
    /// their spreadsheet row numbers, blank rows left out.
    /// </summary>
    public static List<(int Line, object?[] Values)> ReadSheet(Stream file)
    {
        ClosedXML.Excel.XLWorkbook wb;
        try
        {
            var ms = new MemoryStream();
            file.CopyTo(ms);
            ms.Position = 0;
            wb = new ClosedXML.Excel.XLWorkbook(ms);
        }
        catch (Exception)
        {
            throw new InvalidValueException("That file could not be read as an Excel workbook (.xlsx). " +
                                            "Download the template and fill it in.");
        }
        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.TabActive) ?? wb.Worksheets.FirstOrDefault()
                     ?? throw new InvalidValueException("The workbook has no sheet to read.");
            var rows = new List<(int, object?[])>();
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (int r = 2; r <= lastRow; r++)
            {
                var values = new object?[lastCol];
                for (int c = 1; c <= lastCol; c++)
                    values[c - 1] = CellValue(ws.Cell(r, c));
                if (values.Any(v => Text(v).Length > 0))
                    rows.Add((r, values));
            }
            return rows;
        }
    }

    private static object? CellValue(ClosedXML.Excel.IXLCell cell)
    {
        var v = cell.HasFormula ? cell.CachedValue : cell.Value;
        return v.Type switch
        {
            ClosedXML.Excel.XLDataType.Blank => null,
            ClosedXML.Excel.XLDataType.Boolean => v.GetBoolean(),
            ClosedXML.Excel.XLDataType.Number => v.GetNumber(),
            ClosedXML.Excel.XLDataType.Text => v.GetText(),
            ClosedXML.Excel.XLDataType.DateTime => v.GetDateTime(),
            ClosedXML.Excel.XLDataType.TimeSpan => v.GetTimeSpan().ToString(),
            ClosedXML.Excel.XLDataType.Error => v.ToString(),
            _ => v.ToString(),
        };
    }

    private static object? CellAt(object?[] row, int index) => index < row.Length ? row[index] : null;

    /// <summary>
    /// import_rows: sheet rows → review rows, every row kept; a row with a problem is
    /// flagged with row_errors, a repeated name is flagged too.
    /// </summary>
    public static async Task<List<Row>> ImportRowsAsync(List<(int Line, object?[] Values)> sheetRows, string[] columns,
                                                        Func<Row, Task<(Row Values, List<string> Errors)>> clean,
                                                        string[] nameFields)
    {
        if (sheetRows.Count > MaxImportRows)
            throw new InvalidValueException($"The sheet has {sheetRows.Count.ToString("N0", Inv)} rows; import at most " +
                                            $"{MaxImportRows.ToString("N0", Inv)} at a time.");
        var seen = nameFields.ToDictionary(f => f, _ => new HashSet<string>());
        var output = new List<Row>();
        foreach (var (line, values) in sheetRows)
        {
            var raw = new Row();
            for (int i = 0; i < columns.Length; i++)
                raw[columns[i]] = CellAt(values, i);
            var (clean_, errors) = await clean(raw);
            errors.AddRange(Repeats(clean_, seen, nameFields, "in this sheet"));
            clean_["row_line"] = line;
            clean_["row_errors"] = errors;
            output.Add(clean_);
        }
        return output;
    }

    private static List<string> Repeats(Row values, Dictionary<string, HashSet<string>> seen, string[] fields, string where)
    {
        var output = new List<string>();
        foreach (string f in fields)
        {
            string k = Key(values.Get(f));
            if (k.Length == 0) continue;
            if (seen[f].Contains(k))
                output.Add($"'{SingleLine(values.Get(f))}' appears more than once {where}.");
            seen[f].Add(k);
        }
        return output;
    }

    /// <summary>
    /// submitted_rows: the review screen's rows (a JSON list of objects); entries that are not
    /// objects, are marked _delete or are entirely blank are dropped.
    /// </summary>
    public static List<Row> SubmittedRows(string? rawJson)
    {
        const string unreadable = "The rows sent were not readable — reload the import and try again.";
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrEmpty(rawJson) ? "[]" : rawJson);
        }
        catch (JsonException)
        {
            throw new InvalidValueException(unreadable);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidValueException(unreadable);
            if (doc.RootElement.GetArrayLength() > MaxImportRows)
                throw new InvalidValueException($"At most {MaxImportRows.ToString("N0", Inv)} rows can be saved at a time.");
            var output = new List<Row>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var r = new Row();
                foreach (var p in item.EnumerateObject())
                    r[p.Name] = FromJson(p.Value);
                if (Truthy(r.Get("_delete")))
                    continue;
                if (!r.Any(kv => !kv.Key.StartsWith('_') && Text(kv.Value).Length > 0))
                    continue;
                output.Add(r);
            }
            return output;
        }
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => e.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0 && e.TryGetInt64(out long l) ? (object)l : e.GetDouble(),
        _ => e.Clone(),
    };

    /// <summary>
    /// save_rows: validate every submitted row again and create the good ones. Returns
    /// (created, failed) — failed rows carry row_errors and go back on the review screen.
    /// </summary>
    public static async Task<(int Created, List<Row> Failed)> SaveRowsAsync(List<Row> rows,
        Func<Row, Task<(Row Values, List<string> Errors)>> clean, Func<Row, Task> create, string[] nameFields)
    {
        var seen = nameFields.ToDictionary(f => f, _ => new HashSet<string>());
        int created = 0;
        var failed = new List<Row>();
        foreach (var raw in rows)
        {
            var (values, errors) = await clean(raw);
            errors.AddRange(Repeats(values, seen, nameFields, "in these rows"));
            if (errors.Count > 0)
            {
                values["row_errors"] = errors;
                failed.Add(values);
                continue;
            }
            await create(values);
            created++;
        }
        return (created, failed);
    }

    // ── Products ──────────────────────────────────────────────────────────

    /// <summary>
    /// unit_choices: casefolded name / abbreviation → abbreviation of the active units, plus
    /// the product's own unit even if it was later switched off.
    /// </summary>
    public static async Task<Dictionary<string, string>> UnitChoicesAsync(AccountixDbContext db, string? current = null,
                                                                       CancellationToken ct = default)
    {
        var units = await db.Set<InvUnit>().AsNoTracking().Where(u => u.IsActive == true).OrderBy(u => u.Name)
            .Select(u => new { u.Name, u.Abbreviation }).ToListAsync(ct);
        var choices = new Dictionary<string, string>();
        foreach (var u in units) choices[u.Name.ToLowerInvariant()] = u.Abbreviation;
        foreach (var u in units) choices[u.Abbreviation.ToLowerInvariant()] = u.Abbreviation;
        if (!string.IsNullOrEmpty(current))
            choices.TryAdd(current.ToLowerInvariant(), current);
        return choices;
    }

    /// <summary>category_index: key(name) → id of the active categories.</summary>
    public static async Task<Dictionary<string, int>> CategoryIndexAsync(AccountixDbContext db, CancellationToken ct = default)
    {
        var cats = await db.Set<InvCategory>().AsNoTracking().Where(c => c.IsActive == true).OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var index = new Dictionary<string, int>();
        foreach (var c in cats) index[Key(c.Name)] = c.Id;
        return index;
    }

    /// <summary>
    /// clean_product: (values, errors) for a product from the form, a sheet row or a batch row.
    /// <paramref name="categories"/> maps key(name) → id for imports (the form sends category_id).
    /// </summary>
    public static async Task<(Row Values, List<string> Errors)> CleanProductAsync(
        AccountixDbContext db, Row raw, Dictionary<string, string> choices, Dictionary<string, int>? categories = null,
        int? excludeId = null, bool opening = true, bool upperSku = false, CancellationToken ct = default)
    {
        string sku = SingleLine(raw.Get("sku"));
        var values = new Row
        {
            ["sku"] = upperSku ? sku.ToUpperInvariant() : sku,
            ["name"] = SingleLine(raw.Get("name")),
            ["description"] = Text(raw.Get("description")),
            ["hs_code"] = SingleLine(raw.Get("hs_code")),
            ["category_name"] = SingleLine(raw.Get("category_name")),
            ["category_id"] = null,
        };
        var errors = new List<string>();
        var products = db.Set<InvProduct>();
        if ((string)values["sku"]! == "")
            errors.Add("SKU is required.");
        else if (await TakenAsync(products, nameof(InvProduct.Sku), (string)values["sku"]!, excludeId, ct))
            errors.Add($"SKU '{values["sku"]}' is already used by another product.");
        if ((string)values["name"]! == "")
            errors.Add("Name is required.");
        else if (await TakenAsync(products, nameof(InvProduct.Name), (string)values["name"]!, excludeId, ct))
            errors.Add($"A product named '{values["name"]}' already exists.");
        foreach (var (field, label, whole) in new[]
                 {
                     ("unit_price", "Sale price", false), ("cost_price", "Cost price", false),
                     ("reorder_level", "Reorder level", true), ("weight", "Weight", false),
                 })
        {
            try
            {
                decimal n = Number(raw.Get(field), label, whole: whole);
                values[field] = whole ? (object)(int)n : (double)n;
            }
            catch (InvalidValueException e)
            {
                errors.Add(e.Message);
                values[field] = Text(raw.Get(field));
            }
        }
        if (opening)
        {
            try
            {
                decimal qty = Number(raw.Get("current_stock"), "Opening stock");
                values["current_stock"] = qty;
                if (qty > 0 && values["cost_price"] is double cp && cp <= 0)
                    errors.Add("Opening stock needs a cost price — stock brought on at " +
                               "zero cost would later be sold with no cost of sales.");
            }
            catch (InvalidValueException e)
            {
                errors.Add(e.Message);
                values["current_stock"] = Text(raw.Get("current_stock"));
            }
        }
        string unit = SingleLine(raw.Get("unit"));
        if (choices.Count == 0)
            values["unit"] = unit.Length > 0 ? unit : "pcs";
        else if (unit.Length == 0)
        {
            values["unit"] = choices.GetValueOrDefault("pcs", "");
            if ((string)values["unit"]! == "")
                errors.Add("Unit is required.");
        }
        else if (choices.TryGetValue(unit.ToLowerInvariant(), out string? abbr))
            values["unit"] = abbr;
        else
        {
            values["unit"] = unit;
            errors.Add($"Unit '{unit}' is not set up — add it under Units, or pick one.");
        }
        object? catRaw = raw.Get("category_id");
        if (catRaw is not null && !(catRaw is string cs && cs.Length == 0))
        {
            InvCategory? cat = null;
            if (TryPyInt(catRaw, out int cid))
                cat = await db.Set<InvCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (cat is null)
                errors.Add("That category does not exist.");
            else
                values["category_id"] = cat.Id;
        }
        else if ((string)values["category_name"]! != "")
        {
            int? catId = (categories ?? []).TryGetValue(Key(values["category_name"]), out int id) ? id : null;
            if (catId is null)
                errors.Add($"Category '{values["category_name"]}' not found — add it under Categories first.");
            values["category_id"] = catId;
        }
        errors.AddRange(CheckLength(values, ProductLengths));
        return (values, errors);
    }

    /// <summary>int(x) as Python does it for a form or JSON value.</summary>
    private static bool TryPyInt(object? v, out int result)
    {
        result = 0;
        switch (v)
        {
            case int i: result = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case double d when d == Math.Floor(d) && Math.Abs(d) < int.MaxValue: result = (int)d; return true;
            case double d when !double.IsInfinity(d) && !double.IsNaN(d) && Math.Abs(d) < int.MaxValue: result = (int)Math.Truncate(d); return true;
            case string s: return int.TryParse(s.Trim().Replace("_", ""), NumberStyles.AllowLeadingSign, Inv, out result);
            default: return false;
        }
    }

    /// <summary>opening_date_refusal: why opening stock cannot be dated <paramref name="when"/> (a closed period), or null.</summary>
    public static async Task<string?> OpeningDateRefusalAsync(IPeriods periods, DateTime when, CancellationToken ct = default)
    {
        try
        {
            await periods.RequireOpenAsync(when, "bring opening stock on", ct);
            return null;
        }
        catch (ClosedPeriodException e)
        {
            return e.Message;
        }
    }

    /// <summary>The product a set of cleaned values makes (InvProduct(**values, current_stock=0)).</summary>
    public static InvProduct NewProduct(Row values, DateTime utcNow) => Apply(new InvProduct
    {
        CurrentStock = 0, IsActive = true, CreatedAt = utcNow, UpdatedAt = utcNow,
    }, values, includeCost: true);

    /// <summary>setattr(prod, field, value) for every cleaned field.</summary>
    public static InvProduct Apply(InvProduct p, Row values, bool includeCost)
    {
        p.Sku = (string)values["sku"]!;
        p.Name = (string)values["name"]!;
        p.Description = (string?)values["description"];
        p.HsCode = (string?)values["hs_code"];
        p.CategoryId = values["category_id"] as int?;
        p.UnitPrice = ToDecimal((double)values["unit_price"]!);
        if (includeCost)
            p.CostPrice = ToDecimal((double)values["cost_price"]!);
        p.ReorderLevel = (int)values["reorder_level"]!;
        p.Weight = ToDecimal((double)values["weight"]!);
        p.Unit = (string?)values["unit"];
        return p;
    }

    // ── Units ─────────────────────────────────────────────────────────────

    /// <summary>
    /// clean_unit: (values, errors) for a unit of measure. Name and abbreviation are each
    /// unique ignoring case (products record their unit by abbreviation).
    /// </summary>
    public static async Task<(Row Values, List<string> Errors)> CleanUnitAsync(AccountixDbContext db, Row raw, int? excludeId = null,
                                                                            CancellationToken ct = default)
    {
        var values = new Row
        {
            ["name"] = SingleLine(raw.Get("name")),
            ["abbreviation"] = SingleLine(raw.Get("abbreviation")),
            ["explanation"] = Text(raw.Get("explanation")),
        };
        var errors = new List<string>();
        var units = db.Set<InvUnit>();
        if ((string)values["name"]! == "")
            errors.Add("Name is required.");
        else if (await TakenAsync(units, nameof(InvUnit.Name), (string)values["name"]!, excludeId, ct))
            errors.Add($"Unit '{values["name"]}' already exists.");
        if ((string)values["abbreviation"]! == "")
            errors.Add("Abbreviation is required.");
        else if (await TakenAsync(units, nameof(InvUnit.Abbreviation), (string)values["abbreviation"]!, excludeId, ct))
            errors.Add($"Abbreviation '{values["abbreviation"]}' already exists.");
        errors.AddRange(CheckLength(values, UnitLengths));
        return (values, errors);
    }

    /// <summary>Quantity column rendering: whole numbers as int ("10"), else the float ("2.5").</summary>
    public static string Qty(decimal? q)
    {
        double v = Math.Round((double)(q ?? 0m), 4);
        return v == Math.Floor(v) ? ((decimal)v).ToString("0", Inv) : Float(v);
    }

    /// <summary>A Money column read back (float): "100.0".</summary>
    public static string MoneyStr(decimal? m) => m is decimal d ? Float((double)d) : "None";

    /// <summary>JSON string of a value, for an inline script (Jinja's |tojson).</summary>
    public static string Json(string s) => JsonSerializer.Serialize(s);
}
