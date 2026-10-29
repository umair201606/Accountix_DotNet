using System.Globalization;
using System.Text.RegularExpressions;

namespace Accountix.Parties.Services;

/// <summary>
/// The constants and pure rules of shared/payment_tracking.py: party codes,
/// assignment / payment states, due dates and ageing. Nothing here touches
/// the database.
/// </summary>
public static partial class TrackingRules
{
    public const string Sales = "SI";
    public const string Purchase = "PI";
    /// <summary>An allocation that deliberately points at no invoice (doc_id 0): a forced close.</summary>
    public const string NotApplicable = "NA";
    public const int NaDocId = 0;
    public const decimal Tolerance = 0.01m;

    public static readonly string[] ReceiptVouchers = ["CRV", "BRV", "JV"];
    public static readonly string[] PaymentVouchers = ["CPV", "BPV", "JV"];

    public static readonly IReadOnlyDictionary<string, string> DocForParty =
        new Dictionary<string, string> { ["customer"] = Sales, ["supplier"] = Purchase };
    public static readonly IReadOnlyDictionary<string, string> PartyForDoc =
        new Dictionary<string, string> { [Sales] = "customer", [Purchase] = "supplier" };

    public static readonly string[] AssignStates = ["unassigned", "partial", "assigned", "forced", "review"];
    public static readonly IReadOnlyDictionary<string, string> AssignStateLabels = new Dictionary<string, string>
    {
        ["unassigned"] = "Unassigned",
        ["partial"] = "Partially assigned",
        ["assigned"] = "Assigned",
        ["forced"] = "Force-closed (no invoice)",
        ["review"] = "Needs re-assignment",
    };
    public static readonly string[] DefaultAssignStates = ["unassigned", "partial", "review"];
    public static readonly string[] PayStates = ["unpaid", "partial", "paid"];

    public sealed record Bucket(string Key, string Label, int? Lo, int? Hi);

    public static readonly Bucket[] AgeBuckets =
    [
        new("not_due", "Not due", null, null),
        new("d1_30", "1 – 30 days", 1, 30),
        new("d31_60", "31 – 60 days", 31, 60),
        new("d61_90", "61 – 90 days", 61, 90),
        new("d90_plus", "90+ days", 91, null),
    ];

    // shared/coa.py ENTITY_PARENT_CODES / ENTITY_ID_OFFSET
    public const string CustomerParent = "1-01-02-01";
    public const string SupplierParent = "2-01-01-01";
    public const int EntityIdOffset = 100;

    public static string ParentCode(string kind) => kind == "customer" ? CustomerParent : SupplierParent;

    /// <summary>The deterministic level-5 subledger code of a customer or supplier.</summary>
    public static string EntityCode(string kind, int id) =>
        $"{ParentCode(kind)}-{(id + EntityIdOffset).ToString("D4", CultureInfo.InvariantCulture)}";

    /// <summary>_q: money rounded to cents, half up.</summary>
    public static decimal Q(decimal? value) => Math.Round(value ?? 0m, 2, MidpointRounding.AwayFromZero);

    public static int LedgerPartyId(int accountId) => -accountId;
    public static bool IsLedgerParty(int? partyId) => partyId is int p && p < 0;
    public static int LedgerPartyAccountId(int partyId) => -partyId;

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex TermDays();

    /// <summary>Credit days read out of a free-text payment term ("Net 30"); null when there is none.</summary>
    public static int? TermsDays(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = TermDays().Match(text);
        if (!m.Success) return null;
        if (!long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long days))
            return null;           // longer than any int: well past 3650
        return days is >= 0 and <= 3650 ? (int)days : null;
    }

    /// <summary>effective_due_date: the explicit due date, else issue date + the party's terms, else the issue date.</summary>
    public static DateOnly? EffectiveDueDate(DateOnly? explicitDue, DateOnly? issued, string? partyTerms)
    {
        if (explicitDue is not null) return explicitDue;
        if (issued is null) return null;
        int? days = TermsDays(partyTerms);
        return days is int d && d != 0 ? issued.Value.AddDays(d) : issued;
    }

    public static int OverdueDays(DateOnly? due, DateOnly asOf) =>
        due is null ? 0 : Math.Max(0, asOf.DayNumber - due.Value.DayNumber);

    public static string AgeBucket(int days)
    {
        if (days <= 0) return "not_due";
        foreach (var b in AgeBuckets)
        {
            if (b.Lo is null) continue;
            if (days >= b.Lo && (b.Hi is null || days <= b.Hi)) return b.Key;
        }
        return "d90_plus";
    }

    public static string BucketLabel(string key) => AgeBuckets.FirstOrDefault(b => b.Key == key)?.Label ?? key;

    /// <summary>assign_state: review / unassigned / partial / forced / assigned.</summary>
    public static string AssignState(decimal amount, decimal assigned, decimal forced = 0m)
    {
        forced = Q(forced);
        if (assigned > amount + Tolerance) return "review";
        if (assigned <= Tolerance) return "unassigned";
        if (assigned < amount - Tolerance) return "partial";
        return forced > Tolerance ? "forced" : "assigned";
    }

    public static string PayState(decimal total, decimal settled)
    {
        if (settled <= Tolerance) return "unpaid";
        if (settled >= total - Tolerance) return "paid";
        return "partial";
    }

    /// <summary>_flow_of: "receipt" for a customer credited on CRV/BRV/JV, "payment" for a supplier debited on CPV/BPV/JV.</summary>
    public static string? FlowOf(string partyType, decimal? debit, decimal? credit, string voucherType)
    {
        decimal d = Q(debit), c = Q(credit);
        if (partyType == "customer" && c > Tolerance && ReceiptVouchers.Contains(voucherType)) return "receipt";
        if (partyType == "supplier" && d > Tolerance && PaymentVouchers.Contains(voucherType)) return "payment";
        return null;
    }

    public static DateOnly? AsDate(DateTime? value) => value is DateTime d ? DateOnly.FromDateTime(d) : null;

    /// <summary>_fmt_date: "05 Oct 2026".</summary>
    public static string FmtDate(DateOnly? value) => value?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Python's f"{x:,.2f}".</summary>
    public static string Comma2(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Python's str(float(x)) for a cents amount (what a template prints for a bare float).</summary>
    public static string PyFloat(decimal value)
    {
        string r = ((double)value).ToString("R", CultureInfo.InvariantCulture);
        return r.Contains('.') || r.Contains('E') ? r : r + ".0";
    }
}

/// <summary>A refused assignment: the message is shown to the user verbatim (AllocationError).</summary>
public sealed class AllocationException(string message) : BusinessRuleException(message);
