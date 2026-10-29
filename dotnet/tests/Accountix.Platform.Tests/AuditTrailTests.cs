using Accountix.Core.Entities;
using Accountix.Platform.Audit;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Platform.Tests;

/// <summary>The automatic audit trail (shared/audit.py) and record_now (test_audit_log.py).</summary>
public class AuditTrailTests
{
    [Fact]
    public void Python_json_and_float_repr_are_reproduced()
    {
        Assert.Equal("100.0", PyJson.FloatRepr(100));
        Assert.Equal("1250.75", PyJson.FloatRepr(1250.75));
        Assert.Equal("0.1", PyJson.FloatRepr(0.1));
        Assert.Equal("1e+16", PyJson.FloatRepr(1e16));
        Assert.Equal("1234567890123456.0", PyJson.FloatRepr(1234567890123456));
        Assert.Equal("0.0001", PyJson.FloatRepr(0.0001));
        Assert.Equal("1e-05", PyJson.FloatRepr(0.00001));
        Assert.Equal("-2.5", PyJson.FloatRepr(-2.5));
        Assert.Equal("{\"a\": 1, \"b\": [true, null, \"\\u2022\\u2014\\\"x\"], \"c\": 2.0}",
            PyJson.Dumps(new PyDict { { "a", 1 }, { "b", new object?[] { true, null, "•—\"x" } }, { "c", 2m } }));
    }

    /// <summary>An audit row of the voucher, normalised: voucher id → ID, number → NUM, company → C, creator → ADMIN.</summary>
    private static string Shape(AuditLog r, int voucherId, string number, int admin) =>
        P.Norm($"{r.Action}|{r.Module}|{r.EntityType}|{r.Reference?.Replace(number, "NUM")}|" +
               $"{r.Summary?.Replace(number, "NUM").Replace(voucherId.ToString(), "ID")}|" +
               System.Text.RegularExpressions.Regex.Replace(r.Changes ?? "", "\"company_id\": \\d+", "\"company_id\": C").Replace(number, "NUM"),
               ids: new Dictionary<int, string> { [admin] = "ADMIN", [voucherId] = "ID" });

    [ReferenceFact]
    public async Task A_voucher_lifecycle_is_audited_exactly_as_python_audited_it()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        string num = "JV-AUD-" + P.Tag();
        int before = await SettingsFinanceTests.MaxAuditIdAsync();
        int netId;
        var (scope, db) = P.Scope(host, cid);
        using (scope)
        {
            var v = new AccountingVoucher
            {
                VoucherType = "JV", VoucherNumber = num, VoucherDate = new DateTime(2026, 4, 10, 9, 30, 0), Notes = "first",
                Status = "unapproved", CreatedBy = admin, CreatedAt = new DateTime(2026, 4, 10, 4, 30, 0),
            };
            db.Add(v);
            await db.SaveChangesAsync();
            netId = v.Id;
            v.Notes = "second";
            v.VoucherDate = new DateTime(2026, 4, 11, 9, 30, 0);
            await db.SaveChangesAsync();
            v.Status = "approved";
            await db.SaveChangesAsync();
            v.Status = "unapproved";
            await db.SaveChangesAsync();
            db.Remove(v);
            await db.SaveChangesAsync();
        }

        await using var check = P.Db();
        var net = await check.AuditLogs.Where(a => a.Id > before && a.EntityType == "Accounting voucher" && a.EntityId == netId).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["create", "update", "approve", "unapprove", "delete"], net.Select(a => a.Action));
        Golden.Match("VoucherAudit", net.Select(r => Shape(r, netId, num, admin)).ToList());
        Assert.Contains("{\"field\": \"voucher_date\", \"old\": \"2026-04-10T09:30:00\", \"new\": \"2026-04-11T09:30:00\"}", net[1].Changes);
        Assert.Equal($"Accounting voucher {num} edited (voucher date, notes)", net[1].Summary);
        Assert.All(net, a => Assert.Equal(cid, a.CompanyId));
    }

    [ReferenceFact]
    public async Task One_batched_insert_per_save_and_secrets_redacted()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var (scope, db) = P.Scope(host, cid);
        using (scope)
        {
            for (int i = 0; i < 5; i++)
                db.Add(new ProjectLabel { Name = $"L{i}-{P.Tag()}", IsActive = true, CreatedAt = DateTime.UtcNow });
            var u = await db.Users.FirstAsync(x => x.Id == admin);
            u.PasswordHash = Accountix.Core.Security.PasswordHasher.Hash("Another-pass-1");
            host.Queries.Reset();
            await db.SaveChangesAsync();
            var inserts = host.Queries.Repeated(50).Where(q => q.Contains("INSERT INTO audit_logs")).ToList();
            Assert.Single(inserts);
            Assert.StartsWith("1×", inserts[0]);
        }
        await using var check = P.Db();
        var row = await check.AuditLogs.Where(a => a.EntityType == "User" && a.EntityId == admin).OrderByDescending(a => a.Id).FirstAsync();
        Assert.Equal("[{\"field\": \"password_hash\", \"old\": \"\\u2022\\u2022\\u2022\", \"new\": \"\\u2022\\u2022\\u2022\"}]", row.Changes);
        Assert.DoesNotContain("scrypt", row.Changes);
    }

    [ReferenceFact]
    public async Task Record_now_survives_the_request_rolling_back()
    {
        await using var host = await P.HostAsync();
        var (cid, _) = await P.FreshCompanyAsync(host);
        string tag = "refused-" + P.Tag();
        var (scope, db) = P.Scope(host, cid);
        using (scope)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Add(new ProjectLabel { Name = tag, IsActive = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<Accountix.Core.Shared.IAuditTrail>().RecordNowAsync("refused", "Posting refused " + tag, module: "accounting");
            await tx.RollbackAsync();
        }
        await using var check = P.Db();
        Assert.True(await check.AuditLogs.AnyAsync(a => a.Action == "refused" && a.Summary == "Posting refused " + tag && a.CompanyId == cid));
        Assert.False(await check.AuditLogs.AnyAsync(a => a.Reference == tag));          // the rolled-back create left nothing
    }
}
