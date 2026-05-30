namespace KernelLatencyDetector;

/// <summary>Kind of kernel stall a sample represents.</summary>
public enum StallType
{
    Isr, // Interrupt Service Routine
    Dpc, // Deferred Procedure Call
}
