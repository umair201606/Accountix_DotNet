namespace Accountix.Returns.Tests;

public class ReturnsSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal(Accountix.Core.Entities.Modules.Invoicing, new Accountix.Returns.ReturnsModule().Key);
    }
}
