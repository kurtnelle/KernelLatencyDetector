using KernelLatencyDetector;
using Xunit;

public class DriverStatsTests
{
    [Fact]
    public void Record_AccumulatesCountTotalMaxAvg_PerDriverAndType()
    {
        var stats = new DriverStats();
        stats.Record("a.sys", StallType.Dpc, 100);
        stats.Record("a.sys", StallType.Dpc, 300);

        var row = Assert.Single(stats.GetRanked());
        Assert.Equal("a.sys", row.Driver);
        Assert.Equal(StallType.Dpc, row.Type);
        Assert.Equal(2, row.Count);
        Assert.Equal(400, row.TotalMicros);
        Assert.Equal(300, row.MaxMicros);
        Assert.Equal(200, row.AvgMicros);
    }

    [Fact]
    public void Record_SameDriverDifferentType_ProducesSeparateRows()
    {
        var stats = new DriverStats();
        stats.Record("a.sys", StallType.Isr, 50);
        stats.Record("a.sys", StallType.Dpc, 80);

        Assert.Equal(2, stats.GetRanked().Count);
    }

    [Fact]
    public void GetRanked_OrdersByMaxMicrosDescending()
    {
        var stats = new DriverStats();
        stats.Record("low.sys", StallType.Dpc, 100);
        stats.Record("high.sys", StallType.Dpc, 900);
        stats.Record("mid.sys", StallType.Isr, 400);

        var ranked = stats.GetRanked();
        Assert.Equal("high.sys", ranked[0].Driver);
        Assert.Equal("mid.sys", ranked[1].Driver);
        Assert.Equal("low.sys", ranked[2].Driver);
    }

    [Fact]
    public void GetRanked_EmptyWhenNothingRecorded()
        => Assert.Empty(new DriverStats().GetRanked());
}
