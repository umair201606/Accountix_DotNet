using System.Reflection;
using Accountix.Core.Entities;
using Accountix.Hr.Controllers;
using Accountix.Hr.Services;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Accountix.Hr.Tests;

public class HrSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("hr", new Accountix.Hr.HrModule().Key);
    }

    /// <summary>Every action declares its guard: [Right]/[AnyMember] in module controllers (HR's role checks follow inside), [SignedIn] elsewhere.</summary>
    [Fact]
    public void Every_action_declares_its_guard()
    {
        var controllers = typeof(HrModule).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        var missing = controllers
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
                .Where(m => t.GetCustomAttribute<ModuleAttribute>() is not null
                    ? !m.IsDefined(typeof(RightAttribute)) && !m.IsDefined(typeof(AnyMemberAttribute))
                    : t.GetCustomAttribute<SignedInAttribute>() is null)
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();
        Assert.True(missing.Count == 0, "Unguarded: " + string.Join(", ", missing));
        // the account pages stay open when a company drops HR (module_guard ALWAYS_OPEN)
        Assert.Null(typeof(AccountController).GetCustomAttribute<ModuleAttribute>());
        Assert.Null(typeof(AvatarController).GetCustomAttribute<ModuleAttribute>());
    }

    [Theory]
    [InlineData(7.0, "7.0")]
    [InlineData(2.5, "2.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(8333.33, "8333.33")]
    public void Floats_print_as_python_repr(double v, string expected) => Assert.Equal(expected, Py.Float(v));

    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(3.0, "3")]
    [InlineData(0.0, "0")]
    [InlineData(1234567.0, "1.23457e+06")]
    public void Percent_g_matches_python(double v, string expected) => Assert.Equal(expected, Py.G(v));

    [Theory]
    [InlineData("loan", "Loan")]
    [InlineData("half-day", "Half-Day")]
    [InlineData("ADVANCE", "Advance")]
    public void Title_matches_python(string s, string expected) => Assert.Equal(expected, Py.Title(s));

    [Fact]
    public void Late_and_half_day_follow_the_policy()
    {
        var p = AttendanceRules.Default();
        var day = new DateOnly(2026, 10, 26);
        Assert.Equal((false, false), AttendanceRules.LateAndHalf(day, day.ToDateTime(new TimeOnly(9, 15)), p));
        Assert.Equal((true, false), AttendanceRules.LateAndHalf(day, day.ToDateTime(new TimeOnly(9, 15, 1)), p));
        Assert.Equal((true, true), AttendanceRules.LateAndHalf(day, day.ToDateTime(new TimeOnly(13, 0, 1)), p));
    }

    [Theory]
    [InlineData("10:00", 1900, 10, 0)]
    [InlineData("9:5", 1900, 9, 5)]
    [InlineData("2026-10-26 14:30", 2026, 14, 30)]
    [InlineData("2026-10-26T08:15:00", 2026, 8, 15)]
    public void Corrections_parse_as_strptime(string raw, int year, int hour, int minute)
    {
        var v = AttendanceRules.ParseCorrection(raw)!.Value;
        Assert.Equal((year, hour, minute), (v.Year, v.Hour, v.Minute));
    }

    [Fact]
    public void Unreadable_corrections_are_refused() => Assert.Null(AttendanceRules.ParseCorrection("half past nine"));

    [Fact]
    public void Role_templates_match_access_defaults()
    {
        Assert.Null(AccessDefaults.RoleRights(Role.Admin));
        var mgr = AccessDefaults.RoleRights(Role.Manager)!;
        Assert.True(mgr["leaves"].All(x => x));
        Assert.True(mgr["journal_vouchers"].All(x => !x));
        Assert.True(AccessDefaults.RoleRights(Role.Employee)!.Values.All(a => a.All(x => !x)));
        Assert.Equal("{\"accounting\": false, \"executive\": false, \"fbr\": false, \"finance\": false, \"fixed_assets\": false, \"hr\": true, \"inventory\": false, \"invoicing\": false}",
            AccessDefaults.ModulesJson(AccessDefaults.RoleModules(Role.Employee)!));
    }

    [Fact]
    public void Secure_filename_matches_werkzeug()
    {
        Assert.Equal("avatar_4_1.png", FileStore.SecureFilename("avatar_4_1.png"));
        Assert.Equal("etc_passwd", FileStore.SecureFilename("../../etc/passwd"));
        Assert.Equal("My_cool_movie.mov", FileStore.SecureFilename("My cool movie.mov"));
        Assert.Equal("application/octet-stream", FileStore.SafeType("x.html", "text/html"));
        Assert.Equal("image/png", FileStore.SafeType("x.png", "text/html"));
    }
}
