namespace Accountix.Platform.Tests;

public class PlatformSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal("platform", new Accountix.Platform.PlatformModule().Key);
    }
}
