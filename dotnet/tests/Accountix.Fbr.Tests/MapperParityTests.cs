using System.Security.Cryptography;
using System.Text;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Fbr.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Fbr.Tests;

/// <summary>
/// The payload filed with FBR is byte for byte what the Python app filed for the same invoice: the
/// pretty-printed copy kept in request_json and the body sent over the wire (requests' json.dumps).
/// Python's answers were captured on 29 Oct 2026 (Golden/MapperPayloads.json, Golden/MapperFullYear.json).
/// </summary>
public class MapperParityTests
{
    /// <summary>[json.dumps(p, indent=2, default=str), json.dumps(p)], or ["ERROR", message] when it cannot be built.</summary>
    internal static async Task<string[]> AnswerAsync(FbrMapper mapper, int invoiceId, FbrConfig config)
    {
        try
        {
            var payload = await mapper.BuildAsync(invoiceId, config);
            Assert.NotNull(payload);
            return [Py.Dumps(payload, indent: 2), FbrClient.WireBody(payload)];
        }
        catch (FbrMapper.BuildException e)
        {
            return ["ERROR", e.Message];
        }
    }

    /// <summary>
    /// The crafted invoices (company B, fixed dates and figures), by role. <paramref name="tag"/> keeps
    /// their numbers unique between runs; <see cref="Normalise"/> takes it out again.
    /// </summary>
    internal static async Task<List<(string Role, int Id)>> CasesAsync(string tag) =>
    [
        // Combined tax, a discount, billed (in and out of the tax base), absorbed and expense charges,
        // further tax, a product without an HS code and non-ASCII text.
        ("combined", await FbrData.InvoiceAsync($"SI-{tag}-1",
            [new(2m, 1500m, TaxPct: 18m, Description: "Panneau solaire — 550W “mono”"),
             new(3m, 333.333333m, DiscAmount: 10.005m, TaxPct: 18m, HsCode: null),
             new(1m, 2.675m, TaxPct: 17.5m, Delivery: 100m, Installation: 50.25m)],
            [new(500m), new(120.5m, StTaxable: false, WhtTaxable: true, Description: "Insurance"),
             new(80m, "absorb", false), new(40m, "expense", false), new(0m), new(-5m), new(33.3m, "xyz", null, null, null)],
            globalTaxPct: 18m, totalDiscount: 10.005m, furtherTax: true, furtherPct: 3m)),
        // Per-line tax taken as stored, withholding with a WHT-based charge, paid, fractional quantities.
        ("per_line_paid", await FbrData.InvoiceAsync($"SI-{tag}-2",
            [new(2.5m, 33.333333m, DiscAmount: 0.005m, TaxPct: 18m, Unit: "kg"),
             new(0.1m, 0.2m, TaxPct: 5m, Unit: ""),
             new(1000m, 12345678.123456m, DiscAmount: 1234.56m, TaxPct: 25m)],
            [new(999.99m, WhtTaxable: true)],
            taxMode: "individual", totalTax: 123456.789012m, wht: true, whtPct: 4.5m, paymentStatus: "paid",
            delivery: 150.5m, installation: null)),
        // Empty-ish lines: no quantity or price, no product, no description, no unit; a debit-note number.
        ("debit_note_empty_lines", await FbrData.InvoiceAsync($"SI-dn-{tag}-3",
            [new(null, null, WithProduct: false, Unit: null), new(0m, 0m, WithProduct: false, Description: "Service call"),
             new(1m, 0m, TaxPct: 0m), new(4m, 0.1m, TaxPct: 18m, DiscAmount: 0.4m)],
            taxMode: "", globalTaxPct: null, subtotal: 0.4m, totalDiscount: 0m, customerName: "Walk-in", taxId: null,
            paymentStatus: "partial")),
        // A credit-note number (CN), and one with both (DN wins).
        ("credit_note", await FbrData.InvoiceAsync($"SI-CN-{tag}-4", [new(7m, 0.15m, TaxPct: 16m)], globalTaxPct: 16m)),
        ("cn_and_dn", await FbrData.InvoiceAsync($"CN-DN-{tag}-5", [new(1m, 1.005m, TaxPct: 18m)], globalTaxPct: 18m, wht: true, whtPct: 0.1m)),
        // No payment status: the payload cannot be built (Python's message).
        ("no_payment_status", await FbrData.InvoiceAsync($"SI-NOPAY-{tag}", [new(1m, 10m)], paymentStatus: null)),
    ];

    /// <summary>Role → answer, with the run's tag replaced by "TAG" (nothing else varies: dates and figures are fixed).</summary>
    internal static SortedDictionary<string, string[]> Normalise(IEnumerable<(string Role, string[] Answer)> answers, string tag) =>
        new(answers.ToDictionary(a => a.Role, a => a.Answer.Select(s => s.Replace(tag, "TAG")).ToArray()), StringComparer.Ordinal);

    [ReferenceFact]
    public async Task Payloads_equal_pythons_byte_for_byte()
    {
        await FbrData.ConfigureAsync();
        string tag = Guid.NewGuid().ToString("N")[..5];
        var cases = await CasesAsync(tag);
        await using var db = FbrData.Db();
        var clock = new CompanyClock(db, db.Company, TimeProvider.System);
        var config = await new FbrConfigs(db, clock).GetAsync();
        var mapper = new FbrMapper(db, clock);
        var answers = new List<(string, string[])>();
        foreach (var (role, id) in cases)
            answers.Add((role, await AnswerAsync(mapper, id, config)));
        var normalised = Normalise(answers, tag);
        Assert.Equal("ERROR", normalised["no_payment_status"][0]);
        Golden.Match("MapperPayloads", normalised);
    }

    /// <summary>The FBR settings both apps used for the full-year check (a transient row, never saved).</summary>
    internal static FbrConfig FullYearConfig() => new()
    {
        Environment = "sandbox", AccessToken = "t", SellerNtn = "7654321-0", SellerStrn = "", SellerCnic = "",
        SellerName = "Samba", SellerBusinessName = "Samba Private Limited", SellerAddress = "Lahore",
        SellerPhone = "", SellerEmail = "", IsActive = true,
    };

    /// <summary>A short fingerprint of one invoice's answer (SHA-256 of both strings, first 16 hex digits).</summary>
    internal static string Digest(string[] answer) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n\u0000\n", answer))))[..16];

    /// <summary>
    /// Every sales invoice of Samba's full year (2,760, with every tax mode, charge treatment and payment
    /// state the seed produces) maps to the bytes Python produced: Golden/MapperFullYear.json holds a
    /// fingerprint of Python's two strings per invoice id. Read-only (the settings row is transient).
    /// </summary>
    [PerfFact]
    public async Task Every_invoice_of_a_full_year_maps_as_in_python()
    {
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        var ids = await db.Set<InvInvoice>().OrderBy(i => i.Id).Select(i => i.Id).ToListAsync();
        var mapper = new FbrMapper(db, new CompanyClock(db, company, TimeProvider.System));
        var config = FullYearConfig();
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (int id in ids)
            actual[id.ToString("D6")] = Digest(await AnswerAsync(mapper, id, config));
        Assert.Equal(Perf.Rows("inv_invoices"), actual.Count);
        Golden.Match("MapperFullYear", actual);
    }
}
