# Kernel Latency Detector

A Windows console tool that detects **which device drivers are stalling ("pausing") the
kernel** by holding the CPU too long in **Interrupt Service Routines (ISRs)** and
**Deferred Procedure Calls (DPCs)**.

These stalls are the root cause of audio dropouts, stutter, input lag, and real-time
glitches — the same phenomenon tools like LatencyMon and xperf diagnose. Kernel Latency
Detector runs a short timed capture, attributes every kernel stall to the owning `.sys`
driver, and prints a **worst-first ranked table** so you can pinpoint the culprit, plus a
saved report you can keep or share.

## How it works

The tool opens a real-time **ETW (Event Tracing for Windows)** kernel session — the same
data source the professional tools use — via the
[`Microsoft.Diagnostics.Tracing.TraceEvent`](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent)
library. During the capture window it:

1. Builds an address→driver map from kernel image-load and rundown events.
2. Listens for every ISR and DPC event, reads its routine address and execution time.
3. Resolves each routine address to the owning driver image (`.sys`/`.exe`).
4. Aggregates per `(driver, type)`: count, total, average, and **max single-stall time**.

The headline metric is the **single longest DPC/ISR execution time** per driver — one long
DPC is what actually stalls the kernel. Anything above the threshold (default 500 µs, the
common "real-time audio is in trouble" line) is flagged `** CULPRIT`.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build) or runtime (to run)
- **Administrator privileges** — kernel ISR/DPC tracing requires elevation. The tool
  detects this and self-relaunches with a UAC prompt if you start it without admin.

## Build

```powershell
git clone https://github.com/kurtnelle/KernelLatencyDetector.git
cd KernelLatencyDetector
dotnet build -c Release
```

The executable lands at
`src\KernelLatencyDetector\bin\Release\net8.0\KernelLatencyDetector.exe`.

## Usage

Run from an **Administrator** console (or run it normally and accept the UAC prompt):

```powershell
KernelLatencyDetector.exe [--seconds N] [--threshold US] [--out DIR]
```

| Flag | Default | Description |
|------|---------|-------------|
| `--seconds`   | `30`  | Capture window length, in seconds. |
| `--threshold` | `500` | Culprit cutoff in **microseconds** — drivers whose max stall meets/exceeds this are flagged. |
| `--out`       | current dir | Directory for the saved `.txt` / `.json` reports. |

Press **Ctrl+C** to stop early; results captured so far are still reported.

> Tip: to surface real offenders, exercise the system during the capture — move windows,
> play audio, transfer files, plug in USB/network devices.

### Example

```
> KernelLatencyDetector.exe --seconds 10

Capturing kernel ISR/DPC activity for 10.0s (threshold 500 us)...
Press Ctrl+C to stop early.

Captured 10.0s elevated. Kernel stalls attributed to drivers:

#  Driver            Type  Max us   Avg us   Count   % time   Verdict
1  vmswitch.sys      DPC    621.1     35.6    2812    1.0%   ** CULPRIT
2  tcpip.sys         DPC    136.9     26.1     722    0.2%   ok
3  ntoskrnl.exe      DPC    129.9     18.6    6096    1.1%   ok
4  dxgkrnl.sys       DPC    108.3     11.4    3151    0.4%   ok
5  dxgkrnl.sys       ISR     84.3     27.2    1576    0.4%   ok
...

Worst offender: vmswitch.sys (621 us DPC) - exceeds 500 us threshold.
Report: .\KernelLatencyDetector_2026-05-30_1324.txt / .json
```

In this run the top culprit is `vmswitch.sys` — the Hyper-V virtual switch driver, a common
real-world DPC-latency offender on machines running Hyper-V, WSL2, or
virtualization-based security.

## Real-world example: tracking down a `vmswitch.sys` stall

This is the case that the example output above comes from — a real diagnosis on a
development machine.

- **Suspicion:** the kernel appeared to be pausing periodically, the kind of stall that
  causes audio dropouts and stutter.
- **Capture:** running `KernelLatencyDetector.exe --seconds 10` flagged a single culprit —
  `vmswitch.sys` at **621 µs** max DPC, the only driver over the 500 µs threshold. Every
  other driver was comfortably below it.
- **Identifying it:** `vmswitch.sys` is the **Hyper-V virtual switch**. It isn't installed by
  default — something had enabled the Hyper-V networking stack. On this machine that turned
  out to be **Docker Desktop**, which runs on the WSL2 / Hyper-V backend and brings the
  virtual switch with it.
- **Fix:** uninstalling Docker Desktop tore down the Hyper-V virtual switch, removing
  `vmswitch.sys` and its DPC latency. A follow-up capture confirmed no driver exceeded the
  threshold.

The takeaway: a high-latency driver is often a *symptom* of an installed feature, not a
broken device. Once the tool names the `.sys` file, a quick search for what installs it
usually points straight at the fix — disabling or removing the responsible feature, or
updating the driver.

It's worth remembering that the same symptom can have very different causes. Once upon a
time, this same kind of freezing turned out to be a **failing USB Bluetooth adapter** —
tracked down the hard way, through manual log digging long before this tool existed. Same
stutter, completely different root cause. That's exactly the situation this tool is meant to
short-circuit: instead of guessing, you get the responsible driver named directly.

## Output files

Each run writes a timestamped pair of reports to `--out`:

- `KernelLatencyDetector_<date>_<time>.txt` — the console table, verbatim.
- `KernelLatencyDetector_<date>_<time>.json` — machine-readable: `captureSeconds`,
  `thresholdMicros`, and a `drivers` array with `driver`, `type`, `maxMicros`, `avgMicros`,
  `totalMicros`, `count`, `percentTime`, and `isCulprit` per row.

## Project layout

```
src/KernelLatencyDetector/      # console app
  StallType.cs                  # ISR / DPC enum
  DriverStatRow.cs              # one ranked result row
  DriverModuleMap.cs            # address -> driver interval lookup
  DriverStats.cs                # per-(driver,type) aggregation + ranking
  Reporter.cs                   # console table + JSON rendering
  KernelLatencyTracer.cs        # real-time ETW kernel session
  Program.cs                    # args, self-elevation, orchestration
tests/KernelLatencyDetector.Tests/   # xUnit tests for the pure logic
docs/superpowers/               # design spec + implementation plan
```

## Tests

```powershell
dotnet test
```

The address map, aggregation, and reporting logic are unit-tested (14 tests). The live ETW
capture is verified by running the tool elevated on a real machine.

## License

MIT
