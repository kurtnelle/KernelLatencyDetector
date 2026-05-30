namespace KernelLatencyDetector;

/// <summary>Accumulates kernel-stall samples per (driver, type) and ranks them worst-first.</summary>
public sealed class DriverStats
{
    private sealed class Accumulator
    {
        public long Count;
        public double Total;
        public double Max;
    }

    private readonly Dictionary<(string Driver, StallType Type), Accumulator> _data = new();

    /// <summary>Record one stall sample of the given duration in microseconds.</summary>
    public void Record(string driver, StallType type, double micros)
    {
        var key = (driver, type);
        if (!_data.TryGetValue(key, out var acc))
        {
            acc = new Accumulator();
            _data[key] = acc;
        }
        acc.Count++;
        acc.Total += micros;
        if (micros > acc.Max)
            acc.Max = micros;
    }

    /// <summary>Ranked rows, worst-first by max single-stall duration.</summary>
    public IReadOnlyList<DriverStatRow> GetRanked()
    {
        var rows = new List<DriverStatRow>(_data.Count);
        foreach (var ((driver, type), acc) in _data)
        {
            double avg = acc.Count == 0 ? 0 : acc.Total / acc.Count;
            rows.Add(new DriverStatRow(driver, type, acc.Count, acc.Total, acc.Max, avg));
        }
        rows.Sort(static (a, b) => b.MaxMicros.CompareTo(a.MaxMicros));
        return rows;
    }
}
