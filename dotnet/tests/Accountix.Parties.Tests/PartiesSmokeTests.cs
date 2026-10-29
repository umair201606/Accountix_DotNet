namespace Accountix.Parties.Tests;

public class PartiesSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("invoicing", new Accountix.Parties.PartiesModule().Key);
    }
}
