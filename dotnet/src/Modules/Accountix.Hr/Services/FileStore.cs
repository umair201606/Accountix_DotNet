using Microsoft.AspNetCore.StaticFiles;

namespace Accountix.Hr.Services;

/// <summary>
/// Uploaded files of the active company (shared/file_store.py): kept in
/// stored_files; reads fall back to the old disk layout under UPLOAD_FOLDER
/// (&lt;company&gt;/&lt;key&gt;, &lt;key&gt;, then the bare file name) when that folder is
/// configured. The type a file is served as comes from its own extension,
/// never from the browser, and never one that would run in the page.
/// Callers save; Save/Delete only add to the context.
/// </summary>
public sealed class FileStore(AccountixDbContext db, IConfiguration config, CompanyClock clock)
{
    private static readonly FileExtensionContentTypeProvider Types = new();

    private static readonly HashSet<string> Active =
    [
        "text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml",
        "text/javascript", "application/javascript", "application/x-javascript",
    ];

    public static string SafeType(string key, string? declared = null)
    {
        string guessed = Types.TryGetContentType(key, out var t) ? t : declared ?? "application/octet-stream";
        if (string.IsNullOrEmpty(guessed)) guessed = "application/octet-stream";
        string b = guessed.Split(';')[0].Trim().ToLowerInvariant();
        return Active.Contains(b) || b.StartsWith("text/html") ? "application/octet-stream" : guessed;
    }

    public async Task<StoredFile> SaveAsync(string key, byte[] data, string? contentType, CancellationToken ct = default)
    {
        if (db.Company.CompanyId is null) throw new InvalidOperationException("No active company for file storage");
        var row = await db.Set<StoredFile>().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (row is null)
        {
            row = new StoredFile { Key = key, CreatedAt = clock.UtcNow };
            db.Add(row);
        }
        row.Data = data;
        row.Size = data.Length;
        row.ContentType = SafeType(key, contentType);
        return row;
    }

    public async Task<(byte[] Data, string ContentType)?> ReadAsync(string key, int? companyId = null, CancellationToken ct = default)
    {
        var row = await db.Set<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (row is not null)
            return (row.Data, SafeType(key, row.ContentType));
        foreach (string path in LegacyPaths(key, companyId ?? db.Company.CompanyId))
            if (File.Exists(path))
                return (await File.ReadAllBytesAsync(path, ct), SafeType(path));
        return null;
    }

    public async Task DeleteAsync(string key, int? companyId = null, CancellationToken ct = default)
    {
        await db.Set<StoredFile>().Where(f => f.Key == key).ExecuteDeleteAsync(ct);
        foreach (string path in LegacyPaths(key, companyId ?? db.Company.CompanyId))
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private IEnumerable<string> LegacyPaths(string key, int? companyId)
    {
        string root = config["UPLOAD_FOLDER"] ?? "";
        if (root.Length == 0) yield break;
        var parts = key.Split('/').Where(p => p is not ("" or "." or "..")).ToArray();
        if (parts.Length == 0) yield break;
        if (companyId is int cid)
            yield return Path.Combine([root, cid.ToString(), .. parts]);
        yield return Path.Combine([root, .. parts]);
        if (parts.Length > 1)
            yield return Path.Combine(root, parts[^1]);
    }

    /// <summary>werkzeug.utils.secure_filename: ASCII, path separators to spaces, only [A-Za-z0-9_.-], spaces to '_', no leading dots/underscores.</summary>
    public static string SecureFilename(string filename)
    {
        string s = new string(filename.Normalize(System.Text.NormalizationForm.FormKD).Where(c => c < 128).ToArray());
        s = s.Replace('/', ' ').Replace('\\', ' ');
        s = string.Join("_", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        s = new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-').ToArray()).Trim('.', '_');
        return s;
    }
}
