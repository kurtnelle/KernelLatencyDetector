using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KernelLatencyDetector;

/// <summary>Renders ranked driver stats as a console table and as JSON.</summary>
public static class Reporter
{
    private static double PercentTime(DriverStatRow r, double captureSeconds)
        => captureSeconds <= 0 ? 0 : r.TotalMicros / (captureSeconds * 1_000_000.0) * 100.0;

    private static bool IsCulprit(DriverStatRow r, double thresholdMicros)
        => r.MaxMicros >= thresholdMicros;

    /// <summary>Worst-first human-readable table.</summary>
    public static string FormatTable(
        IReadOnlyList<DriverStatRow> rows, double captureSeconds, double thresholdMicros)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(
            $"Captured {captureSeconds.ToString("0.0", ci)}s elevated. " +
            "Kernel stalls attributed to drivers:");
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine("No kernel stalls were recorded during the capture window.");
            return sb.ToString();
        }

        sb.AppendLine(
            $"{"#",-3}{"Driver",-20}{"Type",-5}{"Max us",10}{"Avg us",10}" +
            $"{"Count",10}{"% time",9}  Verdict");

        int rank = 1;
        foreach (var r in rows)
        {
            string verdict = IsCulprit(r, thresholdMicros) ? "** CULPRIT" : "ok";
            sb.AppendLine(
                $"{rank,-3}{r.Driver,-20}{r.Type.ToString().ToUpperInvariant(),-5}" +
                $"{r.MaxMicros.ToString("0.0", ci),10}{r.AvgMicros.ToString("0.0", ci),10}" +
                $"{r.Count,10}{PercentTime(r, captureSeconds).ToString("0.0", ci) + "%",9}  {verdict}");
            rank++;
        }

        sb.AppendLine();
        var worst = rows[0];
        if (IsCulprit(worst, thresholdMicros))
        {
            sb.AppendLine(
                $"Worst offender: {worst.Driver} " +
                $"({worst.MaxMicros.ToString("0", ci)} us {worst.Type.ToString().ToUpperInvariant()}) " +
                $"- exceeds {thresholdMicros.ToString("0", ci)} us threshold.");
        }
        else
        {
            sb.AppendLine(
                $"No driver exceeded the {thresholdMicros.ToString("0", ci)} us threshold. " +
                "System looks healthy.");
        }

        return sb.ToString();
    }

    /// <summary>Machine-readable report.</summary>
    public static string ToJson(
        IReadOnlyList<DriverStatRow> rows, double captureSeconds, double thresholdMicros)
    {
        var payload = new
        {
            captureSeconds,
            thresholdMicros,
            drivers = rows.Select(r => new
            {
                driver = r.Driver,
                type = r.Type.ToString().ToUpperInvariant(),
                maxMicros = r.MaxMicros,
                avgMicros = r.AvgMicros,
                totalMicros = r.TotalMicros,
                count = r.Count,
                percentTime = PercentTime(r, captureSeconds),
                isCulprit = IsCulprit(r, thresholdMicros),
            }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
