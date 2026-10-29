namespace Accountix.Invoicing.Tests;

public class InvoicingSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("invoicing", new Accountix.Invoicing.InvoicingModule().Key);
    }
}
