using System.Runtime.InteropServices;
using System.Text;

namespace AppTiling;

internal sealed class Config
{
    private const string LayoutSection = "layout";
    private const string WaitSection = "wait";

#if TARGET_DESKTOP
    private const string DesktopSection = "desktop";
    private const string WatchSection = "watch";
#endif

    // Share of the work area width given to the first slot, as a percentage.
    internal int FirstSlotPercent { get; private set; } = 33;

    // Seconds to wait at startup for both windows before giving up.
    internal int WaitTimeoutSeconds { get; private set; } = 60;

    internal int PollIntervalMs { get; private set; } = 500;

#if TARGET_DESKTOP
    // 1-based position of the virtual desktop the windows belong on.
    internal int TargetDesktop { get; private set; } = 4;

    internal int WatchIntervalMs { get; private set; } = 1000;

    internal int WatchTimeoutSeconds { get; private set; } = 600;
#endif

    internal static string FilePath =>
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "config.ini");

    internal static Config Load()
    {
        var config = new Config();
        var path = FilePath;

        try
        {
            if (!File.Exists(path))
            {
                config.WriteDefaultFile(path);
                Log.Write($"No config.ini found - wrote one with the defaults at {path}");
                return config;
            }

            config.FirstSlotPercent =
                ReadInt(path, LayoutSection, "firstSlotPercent", config.FirstSlotPercent, 10, 90);
            config.WaitTimeoutSeconds =
                ReadInt(path, WaitSection, "timeoutSeconds", config.WaitTimeoutSeconds, 1, 3_600);
            config.PollIntervalMs =
                ReadInt(path, WaitSection, "pollIntervalMs", config.PollIntervalMs, 100, 10_000);

#if TARGET_DESKTOP
            config.TargetDesktop =
                ReadInt(path, DesktopSection, "targetDesktop", config.TargetDesktop, 1, 64);
            config.WatchIntervalMs =
                ReadInt(path, WatchSection, "intervalMs", config.WatchIntervalMs, 200, 60_000);
            config.WatchTimeoutSeconds =
                ReadInt(path, WatchSection, "timeoutSeconds", config.WatchTimeoutSeconds, 5, 86_400);
#endif

            Log.Write($"Config: split {config.FirstSlotPercent}/{100 - config.FirstSlotPercent}, " +
                      $"wait up to {config.WaitTimeoutSeconds}s, checking every {config.PollIntervalMs}ms");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read config.ini ({ex.Message}) - using defaults.");
        }

        return config;
    }

    private static int ReadInt(string path, string section, string key, int fallback, int min, int max)
    {
        var buffer = new StringBuilder(64);
        GetPrivateProfileStringW(section, key, string.Empty, buffer, buffer.Capacity, path);

        var text = buffer.ToString().Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        if (!int.TryParse(text, out var value))
        {
            Log.Write($"config.ini [{section}] {key}='{text}' is not a number - using {fallback}.");
            return fallback;
        }

        if (value < min || value > max)
        {
            var clamped = Math.Clamp(value, min, max);
            Log.Write($"config.ini [{section}] {key}={value} is outside {min}-{max} - using {clamped}.");
            return clamped;
        }

        return value;
    }

    private void WriteDefaultFile(string path)
    {
        var contents =
            $"""
            ; App Tiling
            ;
            ; Edit and re-run.
            ; No rebuild needed.

            [{LayoutSection}]
            ; Percentage of the screen width given to the first target application.
            ; Target application names: Program.cs:17-18.
            ; 33 means a 33/67 split.
            ; Range 10-90.
            firstSlotPercent={FirstSlotPercent}

            [{WaitSection}]
            ; How long to wait at startup for both windows to appear before tiling whatever is there.
            ; This returns the instant both are found, so this only costs anything when one of them is not running.
            ; Seconds, range 1-3600.
            timeoutSeconds={WaitTimeoutSeconds}

            ; How often to re-check while waiting.
            ; Milliseconds, range 100-10000.
            pollIntervalMs={PollIntervalMs}

            """;

        File.WriteAllText(path, contents, Encoding.UTF8);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileStringW(
        string section, string key, string defaultValue,
        StringBuilder returnedString, int size, string fileName);
}
