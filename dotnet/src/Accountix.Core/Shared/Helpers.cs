using System.Globalization;
using System.Text;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Core.Shared;

/// <summary>Money as every surface prints it (shared/formatting.py). Negatives in brackets.</summary>
public static class Money
{
    public const string Western = "en";
    public const string Indian = "hi";
    public const string ExcelWestern = "#,##0.00;(#,##0.00)";
    public const string ExcelIndian = "[>=10000000]##\\,##\\,##\\,##0.00;[>=100000]##\\,##\\,##0.00;#,##0.00";

    public static string Format(decimal? value, int decimals = 2, string? numberFormat = Western, bool brackets = true)
    {
        decimal v = value ?? 0m;
        if (decimals < 0) decimals = 0;
        bool negative = v < 0;
        decimal magnitude = Math.Round(Math.Abs(v), decimals, MidpointRounding.ToEven);
        string text;
        if (numberFormat == Indian)
        {
            string fixedText = magnitude.ToString("F" + decimals, CultureInfo.InvariantCulture);
            string intPart = fixedText.Split('.')[0];
            text = GroupIndian(intPart) + (decimals > 0 ? "." + fixedText.Split('.')[1] : "");
        }
        else
            text = magnitude.ToString("N" + decimals, CultureInfo.InvariantCulture);
        if (!negative || magnitude == 0)
            return text;
        return brackets ? $"({text})" : $"-{text}";
    }

    public static string Format(double? value, int decimals = 2, string? numberFormat = Western, bool brackets = true) =>
        Format(value is null ? null : (decimal)value.Value, decimals, numberFormat, brackets);

    public static string ExcelFormat(string? numberFormat) => numberFormat == Indian ? ExcelIndian : ExcelWestern;

    private static string GroupIndian(string digits)
    {
        if (digits.Length <= 3) return digits;
        string head = digits[..^3], tail = digits[^3..];
        var groups = new List<string>();
        while (head.Length > 0)
        {
            groups.Insert(0, head.Length > 2 ? head[^2..] : head);
            head = head.Length > 2 ? head[..^2] : "";
        }
        return string.Join(",", groups) + "," + tail;
    }

    /// <summary>Round half away from zero to 2 places, as Python's Decimal quantize(ROUND_HALF_UP) in the posting code.</summary>
    public static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The company's clock (shared/clock.py): now/today in the company's time zone (default Asia/Karachi).</summary>
public sealed class CompanyClock(AccountixDbContext db, CompanyContext company, TimeProvider time)
{
    public const string DefaultZone = "Asia/Karachi";
    private TimeZoneInfo? _zone;
    private int? _zoneFor;

    public TimeZoneInfo Zone
    {
        get
        {
            if (_zone is not null && _zoneFor == company.CompanyId)
                return _zone;
            string? name = company.CompanyId is int cid
                ? db.Companies.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Timezone).FirstOrDefault()
                : null;
            _zone = Find(name) ?? Find(DefaultZone) ?? TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT");
            _zoneFor = company.CompanyId;
            return _zone;
        }
    }

    /// <summary>Naive local date-time, as Python's local_now().</summary>
    public DateTime Now => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime, DateTimeKind.Unspecified);
    public DateOnly Today => DateOnly.FromDateTime(Now);
    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    private static TimeZoneInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}

/// <summary>Document dates (shared/posting_helpers.py): 'YYYY-MM-DD' → midnight; blank → fallback → today; malformed refused.</summary>
public static class DocumentDates
{
    public static DateTime Parse(string? value, DateTime? fallback, CompanyClock clock)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (fallback?.Date ?? clock.Today.ToDateTime(TimeOnly.MinValue));
        string s = value.Trim();
        if (s.Length > 10) s = s[..10];
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw new DocumentDateException($"'{value}' is not a valid date (YYYY-MM-DD).");
    }
}

/// <summary>
/// Document numbers (shared/doc_numbers.py): a placeholder ("~" + 24 hex)
/// until the insert gives the row its id, then PREFIX-YYYYMM-&lt;id:0000&gt;
/// (YYYYMM by the company clock), in the same transaction.
/// </summary>
public static class DocNumbers
{
    public static string Placeholder() => "~" + Guid.NewGuid().ToString("N")[..24];
    public static bool IsPlaceholder(string? value) => !string.IsNullOrEmpty(value) && value.StartsWith('~');
    public static string FromId(string prefix, DateTime companyNow, int id) => $"{prefix}-{companyNow:yyyyMM}-{id:D4}";
}

/// <summary>
/// Row locks and queues (shared/concurrency.py). The advisory-lock keys are
/// computed exactly as Python computes them, so a .NET request and a Python
/// request queue behind each other while both apps share the database.
/// </summary>
public static class DbLocks
{
    /// <summary>serialize(scope, *key): transaction-scoped advisory lock (Postgres; call inside a transaction).</summary>
    public static async Task SerializeAsync(this AccountixDbContext db, string scope, params object?[] key)
    {
        if (!db.Database.IsNpgsql())
            return;
        int a = (int)(Crc32(Encoding.UTF8.GetBytes(scope)) & 0x7FFFFFFF);
        int b = (int)(Crc32(Encoding.UTF8.GetBytes(PythonTupleRepr(key))) & 0x7FFFFFFF);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({a}, {b})");
    }

    /// <summary>
    /// scoped_get(model, pk, for_update=True): lock the row (FOR NO KEY UPDATE)
    /// until commit, then load it fresh and tracked. Company tables are
    /// filtered as usual; null when missing or another company's.
    /// </summary>
    public static async Task<T?> LockAsync<T>(this AccountixDbContext db, int id, CancellationToken ct = default) where T : class
    {
        var et = db.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped");
        if (db.Database.IsNpgsql())
        {
            // A table name can't be a parameter: it comes from the EF model and is checked; the id is a parameter.
            string sql = $"SELECT 1 FROM {QuoteIdentifier(et.GetTableName()!)} WHERE id = {{0}} FOR NO KEY UPDATE";
            await db.Database.ExecuteSqlRawAsync(sql, [id], ct);
        }
        var found = await db.Set<T>().FirstOrDefaultAsync(e => EF.Property<int>(e, "Id") == id, ct);
        if (found is not null)
            await db.Entry(found).ReloadAsync(ct);
        return found;
    }

    /// <summary>
    /// A table or column name for raw SQL, double-quoted. Names can't be SQL parameters, so only plain
    /// lower-case names (letters, digits, underscores, as every name in this schema is) are accepted.
    /// </summary>
    public static string QuoteIdentifier(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_]*$")
            ? "\"" + name + "\""
            : throw new ArgumentException($"Not a plain SQL name: {name}", nameof(name));

    internal static string PythonTupleRepr(object?[] key)
    {
        var parts = key.Select(PythonRepr).ToList();
        return parts.Count == 1 ? $"({parts[0]},)" : $"({string.Join(", ", parts)})";
    }

    private static string PythonRepr(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => PythonStringRepr(s),
        int or long or short => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "None",
    };

    /// <summary>
    /// Python's repr() of a str: single quotes, unless the text has a ' and no ", then double
    /// quotes; backslash, the chosen quote and \n \r \t escaped. Lock keys must match Python's
    /// byte for byte, or the two apps take different advisory locks for the same thing.
    /// </summary>
    internal static string PythonStringRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(q);
        foreach (char c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c == q => "\\" + c,
                _ when c < ' ' || c == '\x7f' => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        return sb.Append(q).ToString();
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in data) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}

/// <summary>Answers in words for database conflicts (shared/concurrency.conflict_message).</summary>
public static class ConflictMessages
{
    public static string? For(Exception e)
    {
        string? state = (e as Npgsql.PostgresException ?? e.InnerException as Npgsql.PostgresException)?.SqlState;
        return state switch
        {
            "40P01" or "55P03" or "40001" => "Another user changed the same records at the same moment. Nothing was saved — please try again.",
            "23505" => "This clashes with a record that already exists. Nothing was saved.",
            "23503" => "A record this depends on was removed by another user. Nothing was saved.",
            _ => e is DbUpdateConcurrencyException
                ? "Another user changed the same records at the same moment. Nothing was saved — please try again."
                : null,
        };
    }
}
