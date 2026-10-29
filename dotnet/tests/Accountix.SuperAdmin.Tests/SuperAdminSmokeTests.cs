namespace Accountix.SuperAdmin.Tests;

public class SuperAdminSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("superadmin", new Accountix.SuperAdmin.SuperAdminModule().Key);
    }
}
