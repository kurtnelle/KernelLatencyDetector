# Kernel Latency Detector — Design

**Date:** 2026-05-30
**Status:** Approved

## Purpose

A Windows console tool that detects which **device drivers are stalling ("pausing") the
kernel** by holding the CPU too long in Interrupt Service Routines (ISRs) and Deferred
Procedure Calls (DPCs). These stalls are the root cause of audio dropouts, stutter, and
real-time glitches — the same phenomenon LatencyMon/xperf diagnose.

The tool runs a fixed-duration capture, attributes each kernel stall to the owning `.sys`
driver, and produces a ranked **worst-first** list so the user can pinpoint the culprit
driver. It also writes a saved report file.

## Goal (locked)

- **Primary:** pinpoint the specific culprit driver, ranked worst-first.
- Run model: **timed capture → ranked console table + saved report file**.

## Constraints (locked)

- Runs **elevated (Administrator)** — required for kernel ISR/DPC ETW tracing.
- **NuGet dependency allowed**: `Microsoft.Diagnostics.Tracing.TraceEvent`.

## Approach

Real-time ETW via TraceEvent (chosen over offline `wpr.exe` capture and `xperf`, which add
moving parts / external installs for no accuracy gain).

The app opens a live kernel ETW session, listens to ISR/DPC events plus image-load events,
attributes each stall to the owning driver in memory, then ranks.

## Architecture & data flow

```
KernelLatencyDetector.exe  (elevated console app, one-shot timed capture)
   │
   ├─ Admin check + self-elevate (UAC) if not elevated
   │
   ├─ KernelLatencyTracer
   │     ├─ opens real-time kernel ETW session
   │     │     keywords: Interrupt (ISR) + DPC + ImageLoad
   │     ├─ on session start: kernel-image rundown builds a
   │     │     [base..base+size) → driver.sys address map
   │     ├─ ISR event  → look up routine addr → driver → record sample
   │     ├─ DPC event  → look up routine addr → driver → record sample
   │     └─ runs for N seconds (default 30, override via arg)
   │
   ├─ DriverStats aggregator
   │     per driver: count, total µs, max single µs, avg µs (ISR & DPC)
   │
   └─ Reporter
         ├─ console: ranked table, worst-first by max stall
         └─ report file: timestamped .txt + .json next to the exe
```

**Headline metric:** the **single longest DPC/ISR execution time per driver** — one long DPC
is what actually stalls the kernel. Secondary columns: total time, % of capture, sample count.

## Location & identity

- Folder: `I:\Source\repos\KernelLatencyDetector`
- Own `.sln` / `.csproj`; namespace `KernelLatencyDetector`; output `KernelLatencyDetector.exe`.
- Report files: `KernelLatencyDetector_YYYY-MM-DD_HHMM.txt` and `.json`.

## Components

- **Target framework:** `net8.0` (net5 is end-of-life). Detect installed SDK before
  committing; fall back to net6/net7 if net8 SDK is unavailable.
- **NuGet:** `Microsoft.Diagnostics.Tracing.TraceEvent`.

Files (new project):

- `Program.cs` — arg parsing (`--seconds`, `--out`, `--threshold`), admin check + UAC
  self-elevation, orchestration.
- `KernelLatencyTracer.cs` — owns the kernel `TraceEventSession` (real-time). Subscribes to
  `PerfInfoISR`, `PerfInfoDPC`/`ThreadedDPC`, and `ImageLoad`/rundown. Resolves each routine
  address → driver via the module map.
- `DriverModuleMap.cs` — interval map of `[ImageBase, ImageBase+ImageSize) → fileName`;
  `Resolve(ulong addr)`.
- `DriverStats.cs` — per-driver accumulators (ISR + DPC: count, total, max, avg).
- `Reporter.cs` — console table + `.txt`/`.json` report writer.

## Metric & ranking

- Rank drivers by **max single DPC/ISR µs** (worst-first).
- A driver crossing `--threshold` (default **500 µs** — common "real-time audio in trouble"
  line) is flagged `⚠ CULPRIT`.

### Sample console output

```
Captured 30.0s elevated. Kernel stalls attributed to drivers:

#  Driver            Type  Max µs   Avg µs   Count   % time   Verdict
1  nvlddmkm.sys      DPC    1842.3    210.4   48210   12.3%   ⚠ CULPRIT
2  ndis.sys          DPC     612.0     88.1   91022    6.1%   ⚠ CULPRIT
3  USBPORT.sys       ISR     145.7     22.9  150331    2.0%   ok
...
Worst offender: nvlddmkm.sys (1842 µs DPC) — exceeds 500 µs threshold.
Report: KernelLatencyDetector_2026-05-30_1230.txt / .json
```

## Error handling

- **Not elevated** → relaunch self with `runas` (UAC prompt); if declined, exit with a clear
  message.
- **ETW session already exists / leftover** (prior crash) → stop+recreate the named session
  on startup.
- **Address resolves to no module** → bucket as `unknown` rather than crash.
- **Ctrl+C during capture** → stop session cleanly and still print/save partial results.

## Testing

ETW + kernel + admin cannot be cleanly unit-tested, so:

- **Unit tests (pure logic):**
  - `DriverModuleMap` interval lookup — boundary cases (addr at base, at base+size-1, between
    modules, below/above all).
  - `DriverStats` aggregation — max/avg/total/count correctness with synthetic samples.
- **Manual/integration verification:** run elevated for a short capture on this machine;
  confirm a sane ranked table (e.g. `ntoskrnl.exe`, `nvlddmkm.sys`, `ndis.sys` with plausible
  numbers).

## Out of scope (YAGNI)

- Live-updating LatencyMon-style UI (chosen run model is one-shot timed capture).
- Pass/fail-only mode and continuous background monitoring/alerting.
- Cross-platform support (Windows-only by nature of ETW kernel tracing).
- Friendly device-name mapping beyond the `.sys` filename (can be added later).
