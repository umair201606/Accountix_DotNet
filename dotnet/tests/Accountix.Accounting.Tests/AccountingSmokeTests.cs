namespace Accountix.Accounting.Tests;

public class AccountingSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("accounting", new Accountix.Accounting.AccountingModule().Key);
    }
}
