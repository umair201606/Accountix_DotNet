namespace Accountix.Finance.Tests;

public class FinanceSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("finance", new Accountix.Finance.FinanceModule().Key);
    }
}
