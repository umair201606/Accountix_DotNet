using System.Net;
using System.Net.Http.Headers;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>Employee documents (/digital-files): stored in stored_files (the layout Python reads), access per person and role.</summary>
public class DigitalFilesTests
{
    private static readonly byte[] Pdf = "%PDF-1.4\n% test document\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n"u8.ToArray();

    private static async Task<World> OfficeAsync()
    {
        var w = await World.CreateAsync("Files",
            ("admin", "Ayesha Admin", Role.Admin), ("mgr", "Bilal Manager", Role.Manager),
            ("emp1", "Kamran Employee", Role.Employee), ("emp2", "Sana Employee", Role.Employee));
        await using var db = w.Db();
        db.Add(new FileCategory { Name = "CNIC", Description = "Identity" });
        db.Add(new FileCategory { Name = "Degree" });
        await db.SaveChangesAsync();
        return w;
    }

    private static async Task<HttpResponseMessage> UploadAsync(PageClient c, string token, byte[] data, string fileName, string contentType,
                                                               params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        form.Add(new StringContent(token), "__RequestVerificationToken");
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/digital-files/upload") { Content = form });
    }

    private static string Flash(string page, string text) => page.Contains(text) ? text : throw new Xunit.Sdk.XunitException($"flash '{text}' missing");

    [ReferenceFact]
    public async Task An_upload_lands_in_stored_files_and_is_served_back_byte_for_byte()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        int category;
        await using (var db = w.Db()) category = (await db.Set<FileCategory>().FirstAsync(c => c.Name == "CNIC")).Id;
        var emp = w.Client(host, "emp1");
        string token = await emp.TokenFromAsync("/digital-files/");
        var r = await UploadAsync(emp, token, Pdf, "My CNIC.PDF", "application/pdf",
            ("title", ""), ("category_id", category.ToString()), ("expiry_date", "2026-11-05"), ("notes", "front side"));
        Assert.Equal("/digital-files/", r.Headers.Location!.OriginalString);
        Flash(await emp.GetStringAsync("/digital-files/"), "File uploaded.");

        DigitalFile f;
        StoredFile s;
        await using (var db = w.Db())
        {
            f = await db.Set<DigitalFile>().SingleAsync();
            s = await db.Set<StoredFile>().SingleAsync();
        }
        Assert.Equal((w["emp1"], (int?)category, "My CNIC.PDF", "My CNIC.PDF", (int?)Pdf.Length, "application/pdf", "front side"),
                     (f.UserId, f.CategoryId, f.Title, f.OriginalName, f.FileSize, f.MimeType, f.Notes));
        Assert.Equal((new DateOnly(2026, 11, 5), false, (int?)null), (f.ExpiryDate!.Value, f.IsVerified!.Value, f.VerifiedBy));
        Assert.Matches("^[0-9a-f]{32}\\.pdf$", f.Filename);
        Assert.Equal(($"{w["emp1"]}/{f.Filename}", "application/pdf", Pdf.Length, (int?)w.Company), (s.Key, s.ContentType, s.Size, s.CompanyId));
        Assert.Equal(Pdf, s.Data);

        // .NET serves it as an attachment under its own name.
        var dl = await emp.GetAsync($"/digital-files/download/{f.Id}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(Pdf, await dl.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", dl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", dl.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("My CNIC.PDF", dl.Content.Headers.ContentDisposition.FileNameStar ?? dl.Content.Headers.ContentDisposition.FileName?.Trim('"'));

        // A second upload (a PNG) is served back with its own type.
        byte[] pngBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 0x1a, 10, (byte)'f', (byte)'a', (byte)'k', (byte)'e'];
        r = await UploadAsync(emp, token, pngBytes, "scan.png", "image/png", ("title", "Second scan"));
        Assert.Equal("/digital-files/", r.Headers.Location!.OriginalString);
        int pyFile;
        await using (var db = w.Db()) pyFile = (await db.Set<DigitalFile>().SingleAsync(x => x.Title == "Second scan")).Id;
        var png = await emp.GetAsync($"/digital-files/download/{pyFile}");
        Assert.Equal(pngBytes, await png.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);

        // The list shows both, newest first; the one expiring within 30 days raises the banner.
        string list = await emp.GetStringAsync("/digital-files/");
        Assert.True(list.IndexOf("Second scan", StringComparison.Ordinal) < list.IndexOf("My CNIC.PDF", StringComparison.Ordinal));
        Assert.Contains("<td>CNIC</td>", list);
        Assert.Contains($"<td>{Math.Round(Pdf.Length / 1024.0, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} KB</td>", list);
    }

    [ReferenceFact]
    public async Task Each_person_reaches_only_their_own_documents_and_admins_reach_all()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var emp1 = w.Client(host, "emp1");
        var emp2 = w.Client(host, "emp2");
        var admin = w.Client(host, "admin");
        await UploadAsync(emp1, await emp1.TokenFromAsync("/digital-files/"), Pdf, "contract.pdf", "application/pdf", ("title", "Contract"));
        int id;
        await using (var db = w.Db()) id = (await db.Set<DigitalFile>().SingleAsync()).Id;

        var r = await emp2.GetAsync($"/digital-files/download/{id}");
        Assert.Equal(("/digital-files/", HttpStatusCode.Redirect), (r.Headers.Location!.OriginalString, r.StatusCode));
        Flash(await emp2.GetStringAsync("/digital-files/"), "Access denied.");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/digital-files/download/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/digital-files/download/987654321")).StatusCode);

        string t2 = await emp2.TokenFromAsync("/digital-files/");
        r = await emp2.PostFormWithTokenAsync($"/digital-files/delete/{id}", [], t2);
        Assert.Equal("/digital-files/", r.Headers.Location!.OriginalString);
        Flash(await emp2.GetStringAsync("/digital-files/"), "You can delete only your own documents.");

        // Verify and the admin pages are admins'.
        Assert.Equal("{\"error\":\"Access denied\"}\n", await (await emp1.PostFormWithTokenAsync($"/digital-files/verify/{id}", [], await emp1.TokenFromAsync("/digital-files/"))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await emp1.GetAsync("/digital-files/check-expiry")).StatusCode);
        r = await emp1.GetAsync("/digital-files/admin");
        Assert.Equal("/dashboard", r.Headers.Location!.OriginalString);
        var mgr = w.Client(host, "mgr");
        Assert.Equal("/dashboard", (await mgr.GetAsync("/digital-files/admin")).Headers.Location!.OriginalString);

        string adminPage = await admin.GetStringAsync("/digital-files/admin");
        Assert.Contains("<td>Kamran Employee</td>", adminPage);
        Assert.Contains($"action=\"/digital-files/verify/{id}\"", adminPage);
        var v = await admin.PostFormWithTokenAsync($"/digital-files/verify/{id}", [], await admin.TokenFromAsync("/digital-files/admin"));
        Assert.Equal("{\"success\":true}\n", await v.Content.ReadAsStringAsync());
        await using (var db = w.Db())
        {
            var f = await db.Set<DigitalFile>().SingleAsync();
            Assert.Equal((true, (int?)w["admin"]), (f.IsVerified!.Value, f.VerifiedBy));
            Assert.NotNull(f.VerifiedAt);
        }
        Assert.DoesNotContain($"action=\"/digital-files/verify/{id}\"", await admin.GetStringAsync("/digital-files/admin"));

        // The owner deletes: the record and its bytes go.
        r = await emp1.PostFormWithTokenAsync($"/digital-files/delete/{id}", [], await emp1.TokenFromAsync("/digital-files/"));
        Flash(await emp1.GetStringAsync("/digital-files/"), "File deleted.");
        await using (var db = w.Db())
        {
            Assert.False(await db.Set<DigitalFile>().AnyAsync());
            Assert.False(await db.Set<StoredFile>().AnyAsync());
        }
    }

    [ReferenceFact]
    public async Task Bad_uploads_are_refused_in_pythons_words()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var emp = w.Client(host, "emp1");
        string token = await emp.TokenFromAsync("/digital-files/");
        await UploadAsync(emp, token, "<html><script>alert(1)</script>"u8.ToArray(), "page.html", "text/html");
        Flash(await emp.GetStringAsync("/digital-files/"), "Only PDF, PNG, JPG files allowed.");
        await UploadAsync(emp, token, Pdf, "noext", "application/pdf");
        Flash(await emp.GetStringAsync("/digital-files/"), "Only PDF, PNG, JPG files allowed.");
        await UploadAsync(emp, token, Pdf, "a.pdf", "application/pdf", ("expiry_date", "05/11/2026"));
        Flash(await emp.GetStringAsync("/digital-files/"), "Enter the expiry date as YYYY-MM-DD.");
        var none = await emp.PostFormWithTokenAsync("/digital-files/upload", [new("title", "x")], token);
        Assert.Equal(HttpStatusCode.Redirect, none.StatusCode);
        Flash(await emp.GetStringAsync("/digital-files/"), "No file selected.");
        // Another company's category is dropped, as Python drops it.
        int foreign;
        await using (var db = World.Db(Reference.Company("b")))
        {
            var c = new FileCategory { Name = "Foreign " + w.Tag };
            db.Add(c);
            await db.SaveChangesAsync();
            foreign = c.Id;
        }
        await UploadAsync(emp, token, Pdf, "b.jpeg", "image/jpeg", ("category_id", foreign.ToString()), ("expiry_date", "2027-1-5"));
        await using (var db = w.Db())
        {
            var f = await db.Set<DigitalFile>().SingleAsync();
            Assert.Equal(((int?)null, new DateOnly(2027, 1, 5)), (f.CategoryId, f.ExpiryDate!.Value));
            Assert.Equal("image/jpeg", (await db.Set<StoredFile>().SingleAsync()).ContentType);
        }
    }

    [ReferenceFact]
    public async Task A_file_from_before_the_database_store_is_still_read_from_the_upload_folder()
    {
        var w = await OfficeAsync();
        string root = Path.Combine(Path.GetTempPath(), "hrrec_uploads_" + w.Tag);
        string name = Guid.NewGuid().ToString("N") + ".pdf";
        Directory.CreateDirectory(Path.Combine(root, w.Company.ToString(), w["emp1"].ToString()));
        await File.WriteAllBytesAsync(Path.Combine(root, w.Company.ToString(), w["emp1"].ToString(), name), Pdf);
        int id;
        await using (var db = w.Db())
        {
            var f = new DigitalFile
            {
                UserId = w["emp1"], Title = "Old scan", Filename = name, OriginalName = "old.pdf", FileSize = Pdf.Length,
                MimeType = "application/pdf", Notes = "", IsVerified = false, UploadedAt = DateTime.UtcNow.AddYears(-1),
            };
            db.Add(f);
            await db.SaveChangesAsync();
            id = f.Id;
        }
        Environment.SetEnvironmentVariable("UPLOAD_FOLDER", root);
        try
        {
            await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
            var r = await w.Client(host, "emp1").GetAsync($"/digital-files/download/{id}");
            Assert.Equal(Pdf, await r.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("UPLOAD_FOLDER", null);
            Directory.Delete(root, true);
        }
    }

    /// <summary>check-expiry answers Python's JSON (Golden/files.json, capture/files.py: the same six documents around today).</summary>
    [ReferenceFact]
    public async Task Expiry_check_and_profile_match_python()
    {
        var w = await OfficeAsync();
        await w.EmploymentAsync("emp1", manager: "mgr", department: "Finance");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Karachi"));
        await using (var db = w.Db())
        {
            int n = 0;
            foreach (var (who, days) in new[] { ("emp1", -3), ("emp1", 0), ("emp1", 12), ("emp2", 30), ("emp2", 31), ("emp2", -400) })
                db.Add(new DigitalFile
                {
                    UserId = w[who], Title = $"Doc {n++} é", Filename = $"{Guid.NewGuid():N}.pdf", OriginalName = "d.pdf", FileSize = 10,
                    MimeType = "application/pdf", Notes = "", ExpiryDate = today.AddDays(days), IsVerified = false, UploadedAt = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var admin = w.Client(host, "admin");
        string net = await admin.GetStringAsync("/digital-files/check-expiry");
        // Python lists in its query's order (captured sorted by id; .NET lists by id); ids as "doc<n>", dates as days from today.
        Dictionary<string, string> names;
        await using (var db = w.Db())
            names = (await db.Set<DigitalFile>().Select(f => new { f.Id, f.Title }).ToListAsync())
                .ToDictionary(f => f.Id.ToString(), f => "doc" + System.Text.RegularExpressions.Regex.Match(f.Title, "Doc (\\d+) ").Groups[1].Value);
        string norm = System.Text.RegularExpressions.Regex.Replace(net, "\"id\":(\\d+)", m => $"\"id\":\"{names[m.Groups[1].Value]}\"");
        norm = System.Text.RegularExpressions.Regex.Replace(norm, "\"expiry\":\"(\\d{4}-\\d{2}-\\d{2})\"",
            m => $"\"expiry\":\"today{(DateOnly.Parse(m.Groups[1].Value).DayNumber - today.DayNumber):+0;-0;+0}\"");
        Golden.SameText(Golden.Load("files").GetProperty("check_expiry").GetString()!, norm, "check-expiry");
        var j = World.Json(net);
        Assert.Equal((2, 3), (j.GetProperty("expired_count").GetInt32(), j.GetProperty("expiring_count").GetInt32()));

        string adminPage = await admin.GetStringAsync("/digital-files/admin");
        Assert.Contains("2 file(s) expired. Review required.", adminPage);
        Assert.Contains("3 file(s) expiring within 30 days.", adminPage);
        string mine = await w.Client(host, "emp1").GetStringAsync("/digital-files/");
        Assert.Contains("1 file(s) expiring within 30 days.", mine);    // today itself is not "expiring" on this page

        string profile = await admin.GetStringAsync($"/digital-files/profile/{w["emp1"]}");
        Assert.Contains("<td>Finance</td>", profile);
        Assert.Contains("<td>Bilal Manager</td>", profile);
        Assert.Contains("Documents (3)", profile);
        Assert.Equal(HttpStatusCode.OK, (await w.Client(host, "emp1").GetAsync($"/digital-files/profile/{w["emp1"]}")).StatusCode);
        var other = await w.Client(host, "emp1").GetAsync($"/digital-files/profile/{w["emp2"]}");
        Assert.Equal("/dashboard", other.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/digital-files/profile/{Reference.UserId(Reference.Email("owner_b"))}")).StatusCode);
    }

    [ReferenceFact]
    public async Task Two_deletes_of_one_file_at_once_remove_it_once_without_errors()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var clients = host.ClientsFor(w["emp1"], w.Company, 2);
        await UploadAsync(clients[0], await clients[0].TokenFromAsync("/digital-files/"), Pdf, "x.pdf", "application/pdf");
        int id;
        await using (var db = w.Db()) id = (await db.Set<DigitalFile>().SingleAsync()).Id;
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/digital-files/")));
        var rs = await Race.RunAsync(2, i => clients[i].PostFormWithTokenAsync($"/digital-files/delete/{id}", [], tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Contains(rs, r => r.StatusCode == HttpStatusCode.Redirect);
        await using (var db = w.Db()) Assert.False(await db.Set<StoredFile>().AnyAsync());
    }
}
