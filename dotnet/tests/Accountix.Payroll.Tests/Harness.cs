using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.TestSupport;

namespace Accountix.Payroll.Tests;

/// <summary>A world plus the .NET app (Payroll + the real Accounting ledger) signed in as its admin.</summary>
public class Harness : IAsyncLifetime
{
    public World W { get; private set; } = null!;
    public ModuleHost Host { get; private set; } = null!;
    public PageClient C { get; private set; } = null!;
    private string? _token;

    protected virtual Task<World> BuildAsync() => World.CreateAsync("Payroll Co");
    protected virtual Task SeedAsync() => Task.CompletedTask;

    /// <summary>The modules the host boots: Payroll and the real ledger (plus HR where a test needs its MSS routes).</summary>
    protected virtual System.Reflection.Assembly[] Modules => [typeof(PayrollModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly];

    public async Task InitializeAsync()
    {
        if (!Reference.Available) return;
        W = await BuildAsync();
        await SeedAsync();
        Host = await ModuleHost.StartAsync(Modules);
        C = Host.ClientFor(W.Admin, W.Company);
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    public async Task<string> TokenAsync() => _token ??= await C.TokenFromAsync("/compensation/");

    public async Task<HttpResponseMessage> PostAsync(string url, params (string Key, string Value)[] fields) =>
        await C.PostFormWithTokenAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)), await TokenAsync());

    /// <summary>The preview JSON for one person (or null when they are not in that month's payroll).</summary>
    public async Task<JsonElement?> PreviewOrNullAsync(int month, int year, object? adjustments = null, int? user = null)
    {
        var r = await PostAsync("/compensation/payroll-preview-json", ("month", month.ToString()), ("year", year.ToString()),
                                ("adjustments", JsonSerializer.Serialize(adjustments ?? new { })));
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, body[..Math.Min(400, body.Length)]);
        var doc = JsonDocument.Parse(body).RootElement.Clone();
        int who = user ?? W.Admin;
        foreach (var e in doc.GetProperty("employees").EnumerateArray())
            if (e.GetProperty("user_id").GetInt32() == who)
                return e;
        return null;
    }

    public async Task<JsonElement> PreviewAsync(int month, int year, object? adjustments = null, int? user = null) =>
        await PreviewOrNullAsync(month, year, adjustments, user) ?? throw new Xunit.Sdk.XunitException($"{user ?? W.Admin} not in the {month}/{year} payroll");

    /// <summary>The flash messages the next page shows (after a redirect).</summary>
    public async Task<string> FlashesAsync(string page = "/compensation/")
    {
        string html = await (await C.GetAsync(page)).Content.ReadAsStringAsync();
        return string.Join(" | ", Regex.Matches(html, "<span class=\"app-flash-msg\">(.*?)</span>", RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)));
    }

    public async Task SetComponentsAsync(int user, decimal basic, object[] comps, string effective = "2000-01-01")
    {
        var r = await PostAsync($"/compensation/edit-profile/{user}", ("basic_salary", basic.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                ("effective_from", effective), ("components_json", JsonSerializer.Serialize(comps)));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    public static object Comp(string name, string type, string method, double value, bool taxable) =>
        new Dictionary<string, object> { ["name"] = name, ["type"] = type, ["method"] = method, ["value"] = value, ["is_taxable"] = taxable };
}

public static class JsonExt
{
    public static double D(this JsonElement e, string key) => e.GetProperty(key).GetDouble();
    public static double D(this JsonElement e, string key, string sub) => e.GetProperty(key).GetProperty(sub).GetDouble();
}
