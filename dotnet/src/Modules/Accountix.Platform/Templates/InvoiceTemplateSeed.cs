using Accountix.Platform.Services;
using Accountix.Platform.Templates;

namespace Accountix.Platform.Provisioning;

/// <summary>
/// InvoiceTemplate.seed_defaults: two templates per document type (Combined,
/// Item wise), built from the Classic design. The bodies are the Python
/// builder's own output for the seed options (Templates/DefaultTemplates.g.cs,
/// generated from shared/models/invoice_template.py), so a company provisioned
/// here prints exactly like one provisioned by Python.
/// </summary>
public static class InvoiceTemplateSeed
{
    public static async Task SeedDefaultsAsync(AccountixDbContext db, TimeProvider time, CancellationToken ct = default)
    {
        var now = FirstUse.Micro(time.GetUtcNow().UtcDateTime);
        foreach (string docType in new[] { "sales", "purchase" })
        {
            if (await db.Set<InvoiceTemplate>().AnyAsync(t => t.Type == docType, ct))
                continue;
            foreach (var row in DefaultTemplates.Rows.Where(r => r.Type == docType))
            {
                db.Add(new InvoiceTemplate
                {
                    Name = row.Name, Type = row.Type, BodyHtml = row.Body, IsDefault = row.IsDefault, Design = "classic",
                    AccentColor = row.Accent, OptionsJson = row.OptionsJson, CreatedAt = now, UpdatedAt = now,
                });
                await db.SaveChangesAsync(ct);   // one at a time: Python's insertion order
            }
        }
    }
}
