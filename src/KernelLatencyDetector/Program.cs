using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using KernelLatencyDetector;

// ---- Parse args: --seconds N (default 30), --threshold US (default 500), --out DIR (default cwd)
double seconds = 30, thresholdMicros = 500;
string outDir = Directory.GetCurrentDirectory();
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--seconds": double.TryParse(args[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out seconds); break;
        case "--threshold": double.TryParse(args[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out thresholdMicros); break;
        case "--out": outDir = args[i + 1]; break;
    }
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This tool requires Windows (kernel ETW tracing).");
    return 2;
}

// ---- Elevation: kernel ETW needs Administrator. Self-relaunch via UAC if needed.
if (!IsElevated())
{
    Console.WriteLine("Administrator rights are required. Requesting elevation...");
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = string.Join(' ', args),
            UseShellExecute = true,
            Verb = "runas",
        };
        Process.Start(psi);
        return 0; // elevated instance does the work
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine("Elevation was declined. Cannot capture kernel ISR/DPC data without admin.");
        return 1;
    }
}

// ---- Capture (Ctrl+C stops early but still reports).
Console.WriteLine($"Capturing kernel ISR/DPC activity for {seconds:0.0}s (threshold {thresholdMicros:0} us)...");
Console.WriteLine("Press Ctrl+C to stop early.");

var tracer = new KernelLatencyTracer();
IReadOnlyList<DriverStatRow> rows;
try
{
    rows = tracer.Capture(seconds);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Capture failed: {ex.Message}");
    return 1;
}

// ---- Report to console + files.
string table = Reporter.FormatTable(rows, seconds, thresholdMicros);
Console.WriteLine();
Console.WriteLine(table);

string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
string baseName = Path.Combine(outDir, $"KernelLatencyDetector_{stamp}");
File.WriteAllText(baseName + ".txt", table);
File.WriteAllText(baseName + ".json", Reporter.ToJson(rows, seconds, thresholdMicros));
Console.WriteLine($"Report: {baseName}.txt / .json");
return 0;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
