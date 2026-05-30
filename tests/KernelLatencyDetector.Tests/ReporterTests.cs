using System.Text.Json;
using KernelLatencyDetector;
using Xunit;

public class ReporterTests
{
    private static IReadOnlyList<DriverStatRow> SampleRows() => new[]
    {
        new DriverStatRow("nvlddmkm.sys", StallType.Dpc, 100, 21040, 1842.3, 210.4),
        new DriverStatRow("usbport.sys", StallType.Isr, 1000, 22900, 145.7, 22.9),
    };

    [Fact]
    public void FormatTable_IncludesDriverNamesAndVerdicts()
    {
        string table = Reporter.FormatTable(SampleRows(), captureSeconds: 30, thresholdMicros: 500);
        Assert.Contains("nvlddmkm.sys", table);
        Assert.Contains("usbport.sys", table);
        Assert.Contains("CULPRIT", table);   // nvlddmkm exceeds 500us
        Assert.Contains("ok", table);         // usbport below threshold
    }

    [Fact]
    public void FormatTable_EmptyRows_ReportsNoStalls()
    {
        string table = Reporter.FormatTable(Array.Empty<DriverStatRow>(), 30, 500);
        Assert.Contains("No kernel stalls", table);
    }

    [Fact]
    public void ToJson_ProducesParseableJsonWithRowsAndThreshold()
    {
        string json = Reporter.ToJson(SampleRows(), captureSeconds: 30, thresholdMicros: 500);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(30, root.GetProperty("captureSeconds").GetDouble());
        Assert.Equal(500, root.GetProperty("thresholdMicros").GetDouble());
        var drivers = root.GetProperty("drivers");
        Assert.Equal(2, drivers.GetArrayLength());
        Assert.Equal("nvlddmkm.sys", drivers[0].GetProperty("driver").GetString());
        Assert.True(drivers[0].GetProperty("isCulprit").GetBoolean());
        Assert.False(drivers[1].GetProperty("isCulprit").GetBoolean());
    }
}
