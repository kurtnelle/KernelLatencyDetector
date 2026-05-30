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
