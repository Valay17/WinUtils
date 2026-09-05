using System.Diagnostics;
using System.Threading;

namespace BluetoothBattery;

internal static class Timing
{
    private static readonly AsyncLocal<int> depth_ = new();

    // Stopwatch is backed by QueryPerformanceCounter, typically 10 MHz (100 ns/tick).
    internal static double ResolutionNanoseconds =>
        1_000_000_000.0 / Stopwatch.Frequency;

    internal static void LogResolution() =>
        Diagnostics.Write(
            $"[time] timer: {Stopwatch.Frequency:N0} Hz, " +
            $"{ResolutionNanoseconds:F1} ns per tick, " +
            $"high resolution: {Stopwatch.IsHighResolution}");

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

            depth_ = Timing.depth_.Value;
            Timing.depth_.Value = depth_ + 1;
        }

        public void Dispose()
        {
            if (label_ is null)
            {
                return;
            }

            Timing.depth_.Value = depth_;

            var elapsed = Stopwatch.GetElapsedTime(start_);
            var indent = depth_ == 0 ? string.Empty : new string(' ', depth_ * 2);

            // Four decimals of a millisecond is 100 nanoseconds - the counter's actual resolution.
            Diagnostics.Write($"[time] {indent}{label_}: {elapsed.TotalMilliseconds:F4} ms");
        }
    }
}
