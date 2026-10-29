using System.Numerics;

namespace Accountix.Fbr.Services;

/// <summary>
/// The fbr_invoice_records kept for every attempt (FBRApiClient.record_submission
/// and retry_submission). The caller holds the invoice's row lock and saves.
/// </summary>
public static class FbrSubmissions
{
    /// <summary>record_submission: a new record for this attempt (added, not saved).</summary>
    public static FbrInvoiceRecord Record(AccountixDbContext db, int invoiceId, PyDict payload, FbrAnswer answer,
                                          int submittedBy, string invoiceType, DateTime utcNow)
    {
        var record = new FbrInvoiceRecord
        {
            InvInvoiceId = invoiceId,
            InvoiceType = invoiceType,
            RequestJson = Py.Dumps(payload, indent: 2),
            // A dict is pretty-printed; anything else is kept as it came ("" when empty).
            ResponseJson = answer.Data is PyDict d ? Py.Dumps(d, indent: 2) : RawText(answer.Data),
            SubmittedBy = submittedBy,
            SubmittedAt = utcNow,
            AttemptCount = 1,
            CreatedAt = utcNow,
            // Column defaults Python's model supplies.
            FbrInvoiceNumber = "",
            FbrSubmissionId = "",
            ErrorMessage = "",
        };
        // On an INSERT SQLAlchemy leaves out a None whose column has a default,
        // so FBR's null becomes the default "" here (a retry's UPDATE writes NULL).
        if (answer.Ok)
        {
            record.Status = "success";
            record.FbrInvoiceNumber = Column(Get(answer.Data, "fbrInvoiceNumber", "")) ?? "";
            record.FbrSubmissionId = Column(Get(answer.Data, "submissionId", "")) ?? "";
        }
        else
        {
            record.Status = "failed";
            record.ErrorMessage = ErrorOf(answer.Data) ?? "";
        }
        db.Add(record);
        return record;
    }

    /// <summary>retry_submission: the same record, sent again with the invoice as it is now.</summary>
    public static void Retry(FbrInvoiceRecord record, PyDict payload, FbrAnswer answer, int submittedBy, DateTime utcNow)
    {
        record.RequestJson = Py.Dumps(payload, indent: 2);
        record.AttemptCount = (record.AttemptCount ?? 0) + 1;
        record.ResponseJson = Py.Dumps(answer.Data, indent: 2);
        record.SubmittedAt = utcNow;
        record.SubmittedBy = submittedBy;
        if (answer.Ok)
        {
            record.Status = "success";
            record.ErrorMessage = "";
            record.FbrInvoiceNumber = Column(Get(answer.Data, "fbrInvoiceNumber", ""));
            record.FbrSubmissionId = Column(Get(answer.Data, "submissionId", ""));
        }
        else
        {
            record.Status = "failed";
            record.ErrorMessage = ErrorOf(answer.Data);
        }
    }

    /// <summary>data.get("error", data.get("message", str(data))) for a dict, else str(data).</summary>
    public static string? ErrorOf(object? data) => data is PyDict d
        ? Column(d.TryGetValue("error", out var e) ? e : d.TryGetValue("message", out var m) ? m : Py.Str(d))
        : Py.Str(data);

    private static object? Get(object? data, string key, object? fallback) => data is PyDict d ? d.Get(key, fallback) : fallback;

    private static string RawText(object? data) => data switch
    {
        null => "",
        string s => s,
        _ when !Py.Truthy(data) => "",
        _ => Py.Dumps(data),
    };

    /// <summary>
    /// A JSON value written to a text column, as psycopg2 sends it: a string
    /// as is, None as NULL, a number or bool as its literal. (A dict or list
    /// made Python fail; it is kept as its repr.)
    /// </summary>
    private static string? Column(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        double d => Py.FloatRepr(d),
        long or int or BigInteger => Py.Repr(v),
        _ => Py.Repr(v),
    };
}
