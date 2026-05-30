namespace KernelLatencyDetector;

/// <summary>One ranked result row: aggregated stats for a (driver, type) pair.</summary>
public sealed record DriverStatRow(
    string Driver,
    StallType Type,
    long Count,
    double TotalMicros,
    double MaxMicros,
    double AvgMicros);
