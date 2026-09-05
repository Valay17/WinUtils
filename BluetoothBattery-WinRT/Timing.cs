using System.Diagnostics;

namespace BluetoothBattery;

// Scoped timing, written to the same log as everything else. Costs nothing
// when logging is off: Measure returns a struct, so the common path
// allocates nothing, and with logging off it does not even read the clock.
internal static class Timing
{
    // [ThreadStatic] rather than AsyncLocal: the watchers deliver on
    // thread-pool threads and an async continuation can resume on a
    // different one, so depth is a property of where the line runs from, not
    // of a logical call chain.
    [ThreadStatic]
    private static int depth_;

    // Stopwatch is backed by QueryPerformanceCounter, typically 10 MHz on
    // modern Windows - 100 ns per tick - read from the machine rather than
    // assumed, since a hypervisor or older chip can report something coarser.
    internal static double ResolutionNanoseconds =>
        1_000_000_000.0 / Stopwatch.Frequency;

    internal static void LogResolution() =>
        Diagnostics.Write(
            $"[time] timer: {Stopwatch.Frequency:N0} Hz, " +
            $"{ResolutionNanoseconds:F1} ns per tick, " +
            $"high resolution: {Stopwatch.IsHighResolution}");

    // Returns a struct rather than an interface: boxing it would put an
    // allocation on the timing path itself.
    internal static Scope Measure(string label) => new(label);

    internal readonly struct Scope : IDisposable
    {
        private readonly string? label_;
        private readonly long start_;
        private readonly int depth_;

        internal Scope(string label)
        {
            if (!Diagnostics.LoggingIsOn)
            {
                label_ = null;
                start_ = 0;
                depth_ = 0;
                return;
            }

            label_ = label;
            start_ = Stopwatch.GetTimestamp();
            depth_ = Timing.depth_++;
        }

        public void Dispose()
        {
            if (label_ is null)
            {
                return;
            }

            Timing.depth_ = depth_;

            var elapsed = Stopwatch.GetElapsedTime(start_);
            var indent = depth_ == 0 ? string.Empty : new string(' ', depth_ * 2);

            Diagnostics.Write($"[time] {indent}{label_}: {elapsed.TotalMilliseconds:F4} ms");
        }
    }
}
