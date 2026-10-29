namespace Accountix.Payroll.Tests;

public class PayrollSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("hr", new Accountix.Payroll.PayrollModule().Key);
    }
}
