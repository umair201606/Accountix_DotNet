using System.Reflection;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Accountix.Tests;

/// <summary>
/// Every action of a module controller names the right it needs, or says it
/// is open to any member of the module. A page added later cannot slip
/// through unguarded (same job as the Python app's test_rights_enforced).
/// </summary>
public class RightsCoverageTests
{
    [Fact]
    public void Every_module_action_declares_a_right_or_any_member()
    {
        var missing = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttribute<ModuleAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<NonActionAttribute>() is null)
                .Where(m => !m.IsSpecialName)
                .Where(m => !m.IsDefined(typeof(RightAttribute)) && !m.IsDefined(typeof(AnyMemberAttribute)))
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();
        Assert.True(missing.Count == 0, "No [Right] or [AnyMember] on: " + string.Join(", ", missing));
    }
}
