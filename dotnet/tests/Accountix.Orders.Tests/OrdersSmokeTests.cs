namespace Accountix.Orders.Tests;

public class OrdersSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal(Accountix.Core.Entities.Modules.Invoicing, new Accountix.Orders.OrdersModule().Key);
    }
}
