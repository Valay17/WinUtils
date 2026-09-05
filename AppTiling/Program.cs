using System.Diagnostics;
using System.Text;

namespace AppTiling;

internal static class Program
{
    // Restored windows don't report their final size immediately.
    private const int RestoreSettleMs = 200;

    private const int ExitSuccess = 0;
    private const int ExitIncomplete = 1;
    private const int ExitFatal = 2;

    private static readonly Target[] Targets =
    {
        new("Open Hardware Monitor", new[] { "OpenHardwareMonitor", "OHM" }),
        new("Task Manager", new[] { "Taskmgr" }),
    };

    private sealed record Target(string Label, string[] ProcessNames);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Log.Write("=== App Tiling start ===");

            foreach (var argument in args)
            {
                Log.Write($"Ignoring argument '{argument}' - App Tiling no longer takes any.");
            }

            var config = Config.Load();
            WindowLayout.Configure(config.FirstSlotPercent);

            var found = WaitForWindows(config);

            if (found.Count == 0)
            {
                Log.Write("Neither target application has a visible window - nothing to do.");
                Log.Write("=== App Tiling end (nothing found) ===");
                return ExitIncomplete;
            }

            if (found.Count < Targets.Length)
            {
                var missing = Targets
                    .Where(t => found.All(f => f.Label != t.Label))
                    .Select(t => t.Label);
                Log.Write($"Proceeding with {found.Count} of {Targets.Length} windows. Not running: {string.Join(", ", missing)}");
            }

            var restored = false;
            foreach (var (hwnd, label, _) in found)
            {
                restored |= WindowLayout.EnsureRestored(hwnd, label);
            }

            if (restored)
            {
                Thread.Sleep(RestoreSettleMs);
            }

            var arranged = WindowLayout.Arrange(found, Targets.Length);

            var complete = found.Count == Targets.Length && arranged;
            var exitCode = complete ? ExitSuccess : ExitIncomplete;
            Log.Write($"=== App Tiling end (exit {exitCode}) ===");
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Write($"FATAL: {ex}");
            Log.Write("=== App Tiling end (fatal) ===");
            return ExitFatal;
        }
    }

    private static List<(IntPtr Hwnd, string Label, int Slot)> WaitForWindows(Config config)
    {
        var elapsed = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(config.WaitTimeoutSeconds);
        var announcedWait = false;

        while (true)
        {
            var found = Probe();

            if (found.Count == Targets.Length)
            {
                Log.Write($"All {found.Count} windows present after {elapsed.Elapsed.TotalSeconds:F1}s");
                return found;
            }

            if (elapsed.Elapsed >= timeout)
            {
                Log.Write($"Gave up after {config.WaitTimeoutSeconds}s with {found.Count} of {Targets.Length} window(s) present.");
                return found;
            }

            if (!announcedWait)
            {
                Log.Write($"Waiting for windows (polling every {config.PollIntervalMs}ms, up to {config.WaitTimeoutSeconds}s)");
                announcedWait = true;
            }

            Thread.Sleep(config.PollIntervalMs);
        }
    }

    private static List<(IntPtr Hwnd, string Label, int Slot)> Probe()
    {
        var found = new List<(IntPtr, string, int)>(Targets.Length);

        for (var slot = 0; slot < Targets.Length; slot++)
        {
            var target = Targets[slot];
            var hwnd = ProcessWatcher.FindWindow(target.ProcessNames);
            if (hwnd != IntPtr.Zero)
            {
                found.Add((hwnd, target.Label, slot));
            }
        }

        return found;
    }

#if TARGET_DESKTOP
    // COMPILED OUT - never define TARGET_DESKTOP.

    private static int RunWatch(Config config)
    {
        var elapsed = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(config.WatchTimeoutSeconds);

        Log.Write($"Watching for both windows on desktop {config.TargetDesktop}. " +
                  $"Move them there and this will arrange them and exit. " +
                  $"Giving up after {config.WatchTimeoutSeconds}s.");

        string? lastReason = null;

        while (elapsed.Elapsed < timeout)
        {
            var found = Probe();

            if (found.Count < Targets.Length)
            {
                LogOnce(ref lastReason,
                    $"Waiting: {found.Count} of {Targets.Length} window(s) running.");
            }
            else
            {
                var current = VDesktop.CurrentDesktopNumber();

                if (current is null)
                {
                    LogOnce(ref lastReason, "Waiting: cannot determine the current desktop.");
                }
                else if (current.Value != config.TargetDesktop)
                {
                    LogOnce(ref lastReason,
                        $"Waiting: currently on desktop {current.Value}, want {config.TargetDesktop}.");
                }
                else
                {
                    var states = found
                        .Select(f => (f.Label, On: VDesktop.IsWindowOnCurrentDesktop(f.Hwnd, f.Label)))
                        .ToList();

                    var elsewhere = states.Where(s => s.On == false).Select(s => s.Label).ToList();
                    var unknown = states.Where(s => s.On is null).Select(s => s.Label).ToList();

                    if (unknown.Count > 0)
                    {
                        LogOnce(ref lastReason,
                            $"Waiting: cannot tell which desktop {string.Join(", ", unknown)} is on.");
                    }
                    else if (elsewhere.Count > 0)
                    {
                        LogOnce(ref lastReason,
                            $"Waiting: {string.Join(", ", elsewhere)} not on desktop {config.TargetDesktop} yet.");
                    }
                    else
                    {
                        Log.Write($"Both windows are on desktop {config.TargetDesktop} " +
                                  $"after {elapsed.Elapsed.TotalSeconds:F0}s - arranging.");

                        var restored = false;
                        foreach (var (hwnd, label, _) in found)
                        {
                            restored |= WindowLayout.EnsureRestored(hwnd, label);
                        }

                        if (restored)
                        {
                            Thread.Sleep(RestoreSettleMs);
                        }

                        var ok = WindowLayout.Arrange(found, Targets.Length);
                        var code = ok ? ExitSuccess : ExitIncomplete;
                        Log.Write($"=== App Tiling end (exit {code}) ===");
                        return code;
                    }
                }
            }

            Thread.Sleep(config.WatchIntervalMs);
        }

        Log.Write($"Gave up after {config.WatchTimeoutSeconds}s without both windows " +
                  $"reaching desktop {config.TargetDesktop}. Nothing was arranged.");
        Log.Write($"=== App Tiling end (exit {ExitIncomplete}) ===");
        return ExitIncomplete;
    }

    private static void LogOnce(ref string? last, string message)
    {
        if (last == message)
        {
            return;
        }

        last = message;
        Log.Write(message);
    }

    private sealed class Options
    {
        internal bool Watch { get; private init; }

        internal string Describe() => Watch
            ? "watch (waiting for both windows to reach the target desktop)"
            : "normal (wait for both windows, then arrange)";

        internal static Options Parse(string[] args)
        {
            var watch = false;

            foreach (var argument in args)
            {
                switch (argument.ToLowerInvariant())
                {
                    case "--watch":
                    case "-w":
                        watch = true;
                        break;

                    default:
                        Log.Write($"Ignoring unrecognized argument '{argument}'.");
                        break;
                }
            }

            return new Options { Watch = watch };
        }
    }

#endif

#if MOVE_TO_DESKTOP
    // COMPILED OUT - never define MOVE_TO_DESKTOP.
    private static bool MoveToTargetDesktop(
        IReadOnlyList<(IntPtr Hwnd, string Label, int Slot)> windows, int targetDesktop)
    {
        var desktopId = VDesktop.ResolveDesktopId(targetDesktop);
        if (desktopId is null)
        {
            Log.Write($"Skipping the move to desktop {targetDesktop}; will still arrange the windows.");
            return false;
        }

        var allMoved = true;
        foreach (var (hwnd, label, _) in windows)
        {
            allMoved &= VDesktop.MoveWindow(hwnd, desktopId.Value, label);
        }

        return allMoved;
    }

#endif
}

internal static class Log
{
    private static readonly string LogDirectory = ResolveLogDirectory();

    private static readonly string LogPath = Path.Combine(LogDirectory, "AppTiling.log");

    internal static readonly bool Enabled =
        File.Exists(Path.Combine(LogDirectory, "logging.on"));

    private static readonly object Gate = new();

    private static string ResolveLogDirectory()
    {
        try
        {
            // Environment.ProcessPath, not Assembly.Location: the latter is
            // empty in a single-file published binary.
            var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(executableDirectory))
            {
                return executableDirectory;
            }
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    internal static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }

        Debug.WriteLine(line);
    }
}
