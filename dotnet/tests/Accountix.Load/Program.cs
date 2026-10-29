// Accountix load test: many people working in ONE company at once.
//
//   Accountix.Load run   --base http://127.0.0.1:5321 --db "<npgsql>" --secret <SECRET_KEY> --company 2
//                        [--users 1,6-34] [--vus 30] [--seconds 180] [--think 500-2000] [--hot 4]
//                        [--month 2026-05] [--out dir]
//   Accountix.Load check --db "<npgsql>" --company 2                       data checks + Books Integrity only
//   Accountix.Load seed-users --db "<npgsql>" --company 2 [--count 29] [--admins 9]   the run's people (prints their ids)
//   Accountix.Load size  --base ... --db ... --secret ... --company 2 --user 1 --url /path   page bytes plain/gzip/br
//
// The database must be a WRITABLE copy (e.g. accountix_ref_load made from
// accountix_ref_perf); the run posts vouchers. docs/DEPLOY.md, "Load test".
using System.Globalization;
using Accountix.Load;

var a = Args.Parse(args);
switch (a.Command)
{
    case "run":
    {
        var run = new LoadRun(a);
        var report = await run.ExecuteAsync();
        Console.WriteLine(report);
        return run.Passed ? 0 : 1;
    }
    case "check":
    {
        var checks = await DataChecks.RunAsync(a.Db, a.Company, booksIntegrity: true);
        foreach (var c in checks)
            Console.WriteLine($"{(c.Ok ? "PASS" : "FAIL")}  {c.Name,-28} {c.Detail}");
        return checks.All(c => c.Ok) ? 0 : 1;
    }
    case "seed-users":
    {
        var ids = await LoadUsers.SeedAsync(a.Db, a.Company, a.Count, a.Admins);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ids));
        return 0;
    }
    case "size":
    {
        foreach (var (enc, bytes, ms) in await PageSize.MeasureAsync(a))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{enc,-8} {bytes,10:N0} bytes  {ms,7:F0} ms"));
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: Accountix.Load run|check|seed-users|size --base URL --db CS --secret KEY --company ID ...");
        return 2;
}
