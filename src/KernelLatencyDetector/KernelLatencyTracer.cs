using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace KernelLatencyDetector;

/// <summary>
/// Opens a real-time kernel ETW session, attributes ISR/DPC stalls to drivers,
/// and aggregates them for the given capture duration.
/// </summary>
public sealed class KernelLatencyTracer
{
    private readonly DriverModuleMap _map = new();
    private readonly DriverStats _stats = new();

    /// <summary>Run a blocking capture for <paramref name="seconds"/> and return ranked stats.</summary>
    public IReadOnlyList<DriverStatRow> Capture(double seconds)
    {
        // A leftover kernel session from a prior crash would block EnableKernelProvider.
        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
        session.StopOnDispose = true;

        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Interrupt |
            KernelTraceEventParser.Keywords.DeferedProcedureCalls |
            KernelTraceEventParser.Keywords.ImageLoad);

        var kernel = session.Source.Kernel;

        // Build/extend the address map from rundown (DCStart) and live image loads.
        void OnImage(ImageLoadTraceData d) => _map.Add(d.ImageBase, (ulong)d.ImageSize, FileName(d.FileName));
        kernel.ImageLoad += OnImage;
        kernel.ImageDCStart += OnImage;

        kernel.PerfInfoISR += d => Record(d.Routine, StallType.Isr, d.ElapsedTimeMSec);
        kernel.PerfInfoDPC += d => Record(d.Routine, StallType.Dpc, d.ElapsedTimeMSec);
        kernel.PerfInfoThreadedDPC += d => Record(d.Routine, StallType.Dpc, d.ElapsedTimeMSec);

        // Stop processing after the capture window on a background timer.
        using var timer = new System.Threading.Timer(
            _ => session.Source.StopProcessing(),
            null,
            TimeSpan.FromSeconds(seconds),
            System.Threading.Timeout.InfiniteTimeSpan);

        session.Source.Process(); // blocks until StopProcessing or session stop

        return _stats.GetRanked();
    }

    private void Record(ulong routine, StallType type, double elapsedMSec)
    {
        string driver = _map.Resolve(routine) ?? "unknown";
        _stats.Record(driver, type, elapsedMSec * 1000.0); // ms -> microseconds
    }

    // ImageLoad FileName is a full NT path; reduce to the bare .sys/.exe name.
    private static string FileName(string path)
        => string.IsNullOrEmpty(path) ? "unknown" : System.IO.Path.GetFileName(path);
}
