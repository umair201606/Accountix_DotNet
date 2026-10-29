namespace Accountix.Inventory.Tests;

public class InventorySmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("inventory", new Accountix.Inventory.InventoryModule().Key);
    }
}
