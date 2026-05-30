# Kernel Latency Detector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows console tool that captures kernel ISR/DPC activity for a fixed duration, attributes each kernel stall to the owning `.sys` driver, and prints + saves a worst-first ranked list pinpointing the culprit driver.

**Architecture:** Pure-C# real-time ETW. An elevated console app opens the kernel ETW session, builds a driver address map from image-load/rundown events, attributes each ISR/DPC event to a driver by routine address, aggregates per-(driver,type) stats, and reports. Pure logic (address map, aggregation, formatting) is unit-tested; the ETW wiring and elevation are verified by a manual elevated run.

**Tech Stack:** .NET 8 (`net8.0`), C#, `Microsoft.Diagnostics.Tracing.TraceEvent`, xUnit, `System.Text.Json`.

---

## File Structure

```
I:\Source\repos\KernelLatencyDetector\
├─ KernelLatencyDetector.sln
├─ src\KernelLatencyDetector\
│   ├─ KernelLatencyDetector.csproj   (Exe, net8.0, x64, TraceEvent)
│   ├─ StallType.cs                   (enum Isr/Dpc)
│   ├─ DriverModuleMap.cs             (interval map: addr -> driver .sys)
│   ├─ DriverStats.cs                 (per-(driver,type) accumulators + ranking)
│   ├─ DriverStatRow.cs               (immutable ranked-row record)
│   ├─ Reporter.cs                    (console table + JSON serialization)
│   ├─ KernelLatencyTracer.cs         (owns TraceEventSession, ETW wiring)
│   └─ Program.cs                     (args, admin check/self-elevate, orchestration)
└─ tests\KernelLatencyDetector.Tests\
    ├─ KernelLatencyDetector.Tests.csproj  (net8.0, xunit, ref to main project)
    ├─ DriverModuleMapTests.cs
    ├─ DriverStatsTests.cs
    └─ ReporterTests.cs
```

**Design refinement locked here:** stats are keyed by **(driver, StallType)**. A driver that
produces both long ISRs and long DPCs yields two rows. This removes the ambiguity of "which
type does the Avg/Count column describe" in the spec's single-row sketch — each row's
Max/Avg/Count/%-time all describe one (driver, type) pair. Ranking is worst-first by Max µs.

`%time` is defined as `TotalMicros / (captureSeconds * 1_000_000) * 100` — i.e. fraction of
wall-clock spent in that driver's stalls on a single-core-equivalent basis.

---

### Task 1: Scaffold solution, projects, and dependencies

**Files:**
- Create: `KernelLatencyDetector.sln`
- Create: `src/KernelLatencyDetector/KernelLatencyDetector.csproj`
- Create: `src/KernelLatencyDetector/Program.cs` (temporary stub)
- Create: `tests/KernelLatencyDetector.Tests/KernelLatencyDetector.Tests.csproj`
- Create: `.gitignore`

- [ ] **Step 1: Create the main project**

Run (from `I:\Source\repos\KernelLatencyDetector`):
```bash
dotnet new console -n KernelLatencyDetector -o src/KernelLatencyDetector -f net8.0
```

- [ ] **Step 2: Set csproj properties and add TraceEvent**

Replace `src/KernelLatencyDetector/KernelLatencyDetector.csproj` with:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PlatformTarget>x64</PlatformTarget>
    <RootNamespace>KernelLatencyDetector</RootNamespace>
    <AssemblyName>KernelLatencyDetector</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.1.16" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create the test project**

Run:
```bash
dotnet new xunit -n KernelLatencyDetector.Tests -o tests/KernelLatencyDetector.Tests -f net8.0
dotnet add tests/KernelLatencyDetector.Tests/KernelLatencyDetector.Tests.csproj reference src/KernelLatencyDetector/KernelLatencyDetector.csproj
```

- [ ] **Step 4: Create the solution and add both projects**

Run:
```bash
dotnet new sln -n KernelLatencyDetector
dotnet sln add src/KernelLatencyDetector/KernelLatencyDetector.csproj
dotnet sln add tests/KernelLatencyDetector.Tests/KernelLatencyDetector.Tests.csproj
```

- [ ] **Step 5: Add a .gitignore**

Create `.gitignore`:
```gitignore
bin/
obj/
*.user
.vs/
```

- [ ] **Step 6: Verify the solution builds**

Run: `dotnet build`
Expected: `Build succeeded` with 0 errors (warnings about an unused stub are fine).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: scaffold KernelLatencyDetector solution with TraceEvent + xUnit"
```

---

### Task 2: StallType enum + DriverStatRow record

**Files:**
- Create: `src/KernelLatencyDetector/StallType.cs`
- Create: `src/KernelLatencyDetector/DriverStatRow.cs`

- [ ] **Step 1: Create the enum**

Create `src/KernelLatencyDetector/StallType.cs`:
```csharp
namespace KernelLatencyDetector;

/// <summary>Kind of kernel stall a sample represents.</summary>
public enum StallType
{
    Isr, // Interrupt Service Routine
    Dpc, // Deferred Procedure Call
}
```

- [ ] **Step 2: Create the row record**

Create `src/KernelLatencyDetector/DriverStatRow.cs`:
```csharp
namespace KernelLatencyDetector;

/// <summary>One ranked result row: aggregated stats for a (driver, type) pair.</summary>
public sealed record DriverStatRow(
    string Driver,
    StallType Type,
    long Count,
    double TotalMicros,
    double MaxMicros,
    double AvgMicros);
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/KernelLatencyDetector/KernelLatencyDetector.csproj`
Expected: `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add StallType enum and DriverStatRow record"
```

---

### Task 3: DriverModuleMap (address -> driver interval lookup)

**Files:**
- Create: `src/KernelLatencyDetector/DriverModuleMap.cs`
- Test: `tests/KernelLatencyDetector.Tests/DriverModuleMapTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/KernelLatencyDetector.Tests/DriverModuleMapTests.cs`:
```csharp
using KernelLatencyDetector;
using Xunit;

public class DriverModuleMapTests
{
    private static DriverModuleMap TwoModuleMap()
    {
        var map = new DriverModuleMap();
        map.Add(0x1000, 0x100, "a.sys"); // [0x1000, 0x1100)
        map.Add(0x2000, 0x080, "b.sys"); // [0x2000, 0x2080)
        return map;
    }

    [Fact]
    public void Resolve_AddressAtBase_ReturnsModule()
        => Assert.Equal("a.sys", TwoModuleMap().Resolve(0x1000));

    [Fact]
    public void Resolve_AddressAtLastByte_ReturnsModule()
        => Assert.Equal("a.sys", TwoModuleMap().Resolve(0x10FF));

    [Fact]
    public void Resolve_AddressAtEndExclusive_ReturnsNull()
        => Assert.Null(TwoModuleMap().Resolve(0x1100));

    [Fact]
    public void Resolve_AddressInSecondModule_ReturnsSecond()
        => Assert.Equal("b.sys", TwoModuleMap().Resolve(0x2040));

    [Fact]
    public void Resolve_AddressBelowAll_ReturnsNull()
        => Assert.Null(TwoModuleMap().Resolve(0x0500));

    [Fact]
    public void Resolve_AddressInGapBetweenModules_ReturnsNull()
        => Assert.Null(TwoModuleMap().Resolve(0x1800));

    [Fact]
    public void Add_ZeroSize_IsIgnored()
    {
        var map = new DriverModuleMap();
        map.Add(0x5000, 0, "z.sys");
        Assert.Null(map.Resolve(0x5000));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DriverModuleMapTests`
Expected: FAIL — `DriverModuleMap` does not exist / does not compile.

- [ ] **Step 3: Implement DriverModuleMap**

Create `src/KernelLatencyDetector/DriverModuleMap.cs`:
```csharp
namespace KernelLatencyDetector;

/// <summary>
/// Maps a kernel code address to the loaded driver/image that owns it.
/// Intervals are [Start, End) half-open. Built from ETW image-load + rundown events.
/// </summary>
public sealed class DriverModuleMap
{
    private readonly struct Interval
    {
        public readonly ulong Start;
        public readonly ulong End; // exclusive
        public readonly string Name;
        public Interval(ulong start, ulong end, string name)
        {
            Start = start;
            End = end;
            Name = name;
        }
    }

    private readonly List<Interval> _intervals = new();
    private bool _sorted = true;

    /// <summary>Register a loaded image. Zero-size images are ignored.</summary>
    public void Add(ulong baseAddress, ulong size, string fileName)
    {
        if (size == 0)
            return;
        _intervals.Add(new Interval(baseAddress, baseAddress + size, fileName));
        _sorted = false;
    }

    /// <summary>Resolve an address to a driver file name, or null if unknown.</summary>
    public string? Resolve(ulong address)
    {
        if (!_sorted)
        {
            _intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            _sorted = true;
        }

        // Binary search for the last interval whose Start <= address.
        int lo = 0, hi = _intervals.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_intervals[mid].Start <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
            return null;
        var iv = _intervals[found];
        return address < iv.End ? iv.Name : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DriverModuleMapTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add DriverModuleMap address-to-driver interval lookup"
```

---

### Task 4: DriverStats (aggregation + ranking)

**Files:**
- Create: `src/KernelLatencyDetector/DriverStats.cs`
- Test: `tests/KernelLatencyDetector.Tests/DriverStatsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/KernelLatencyDetector.Tests/DriverStatsTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DriverStatsTests`
Expected: FAIL — `DriverStats` does not exist.

- [ ] **Step 3: Implement DriverStats**

Create `src/KernelLatencyDetector/DriverStats.cs`:
```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DriverStatsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add DriverStats aggregation and worst-first ranking"
```

---

### Task 5: Reporter (console table + JSON)

**Files:**
- Create: `src/KernelLatencyDetector/Reporter.cs`
- Test: `tests/KernelLatencyDetector.Tests/ReporterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/KernelLatencyDetector.Tests/ReporterTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReporterTests`
Expected: FAIL — `Reporter` does not exist.

- [ ] **Step 3: Implement Reporter**

Create `src/KernelLatencyDetector/Reporter.cs`:
```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReporterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS (14 tests total).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add Reporter console table and JSON output"
```

---

### Task 6: KernelLatencyTracer (ETW wiring)

**Files:**
- Create: `src/KernelLatencyDetector/KernelLatencyTracer.cs`

> Not unit-tested: requires a live elevated kernel ETW session. Verified by the manual run in
> Task 8. Keep this class thin — all testable logic already lives in DriverModuleMap/DriverStats.

- [ ] **Step 1: Implement KernelLatencyTracer**

Create `src/KernelLatencyDetector/KernelLatencyTracer.cs`:
```csharp
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
```

- [ ] **Step 2: Build to verify the TraceEvent API usage compiles**

Run: `dotnet build src/KernelLatencyDetector/KernelLatencyDetector.csproj`
Expected: `Build succeeded`. If any event/field name mismatches the installed TraceEvent
(3.1.16), fix to the compiler-suggested member (e.g. `ImageSize`, `Routine`, `ElapsedTimeMSec`
are the canonical names) — do not invent members.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: add KernelLatencyTracer real-time ETW capture"
```

---

### Task 7: Program.cs (args, admin check + self-elevation, orchestration)

**Files:**
- Modify: `src/KernelLatencyDetector/Program.cs` (replace the scaffold stub entirely)

- [ ] **Step 1: Implement Program.cs**

Replace `src/KernelLatencyDetector/Program.cs` with:
```csharp
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
```

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Run the full unit suite (still green)**

Run: `dotnet test`
Expected: PASS (14 tests).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add Program entrypoint with args, self-elevation, orchestration"
```

---

### Task 8: Manual elevated integration verification

**Files:** none (verification only).

> ETW kernel capture cannot be unit-tested. This task is the integration gate. The agent
> should run it and report the actual console output rather than claim success.

- [ ] **Step 1: Publish a runnable build**

Run: `dotnet build -c Release`
Expected: `Build succeeded`.

- [ ] **Step 2: Run a short capture in an elevated console**

In an **Administrator** PowerShell, from the repo root:
```powershell
& ".\src\KernelLatencyDetector\bin\Release\net8.0\KernelLatencyDetector.exe" --seconds 10
```
Expected: a ranked table listing real kernel modules (e.g. `ntoskrnl.exe`, `nvlddmkm.sys`,
`ndis.sys`, `usbport.sys`) with plausible microsecond values, a worst-offender line, and a
`Report: ...` line. Confirm the `.txt` and `.json` files were created.

- [ ] **Step 3: Sanity-check the JSON**

Open the generated `.json` and confirm it has `captureSeconds`, `thresholdMicros`, and a
`drivers` array with `isCulprit` flags.

- [ ] **Step 4: Record results**

If the table is sane, the feature is verified. If `unknown` dominates (address map not built),
confirm the `ImageDCStart` rundown handler is firing; if event/field names mismatch the
installed TraceEvent version, correct them to the compiler-resolved members and re-run.

- [ ] **Step 5: Final commit / tag**

```bash
git add -A
git commit -m "docs: verified elevated kernel latency capture" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage:** pinpoint culprit driver (Tasks 3–5), timed capture (Task 6), console table + saved report (Tasks 5,7), admin/self-elevation (Task 7), leftover-session recovery + unknown bucket + Ctrl+C (Tasks 6,7), unit tests for map/stats/reporter (Tasks 3–5), manual integration (Task 8). All covered.
- **Ambiguity resolved:** stats keyed by (driver, type); `%time` formula defined explicitly.
- **Type consistency:** `Record(string, StallType, double)`, `Resolve(ulong)`, `Add(ulong,ulong,string)`, `GetRanked()`, `FormatTable(rows, captureSeconds, thresholdMicros)`, `ToJson(...)`, `Capture(double)` are used identically across tasks.
