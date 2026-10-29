namespace Accountix.Fbr.Tests;

public class FbrSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal(Accountix.Core.Entities.Modules.Fbr, new Accountix.Fbr.FbrModule().Key);
    }
}
