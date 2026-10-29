namespace Accountix.HrRecords.Tests;

public class HrRecordsSmokeTests
{
    [Fact]
    public void Module_is_registered_under_its_key()
    {
        Assert.Equal(Accountix.Core.Entities.Modules.Hr, new Accountix.HrRecords.HrRecordsModule().Key);
    }
}
