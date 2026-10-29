using System.Reflection;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Accountix.FixedAssets.Tests;

public class FixedAssetsSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("fixed_assets", new FixedAssetsModule().Key);
    }

    [Fact]
    public void Every_action_declares_its_right()
    {
        var actions = typeof(FixedAssetsModule).Assembly.GetTypes()
            .Where(t => typeof(Controller).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
            .ToList();
        Assert.True(actions.Count >= 38, $"{actions.Count} actions");
        foreach (var m in actions)
            Assert.True(m.GetCustomAttributes<RightAttribute>().Any() || m.GetCustomAttribute<AnyRightAttribute>() is not null
                        || m.GetCustomAttribute<AnyMemberAttribute>() is not null, $"{m.DeclaringType!.Name}.{m.Name} has no right");
        foreach (var t in actions.Select(a => a.DeclaringType!).Distinct())
            Assert.NotNull(t.GetCustomAttribute<ModuleAttribute>());
    }
}
