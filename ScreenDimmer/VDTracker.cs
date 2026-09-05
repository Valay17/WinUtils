using Microsoft.Win32;

namespace ScreenDimmer;

internal sealed class VDTracker : IDisposable
{
    private const string AllDesktops = "*";
    private const string ValueName = "CurrentVirtualDesktop";

    private const string AllDesktopsValueName = "VirtualDesktopIDs";

    private const int GuidByteLength = 16;

    private readonly IntPtr window_;
    private string current_ = AllDesktops;
    private bool running_;
    private bool disposed_;

    private RegistryKey? watchedKey_;
    private ManualResetEvent? changeSignal_;
    private RegisteredWaitHandle? changeRegistration_;

    internal VDTracker(IntPtr window)
    {
        window_ = window;
    }

    internal void OnKeyChanged()
    {
        if (!running_)
        {
            return;
        }

        ReadOnce();

        if (Arm() || Arm())
        {
            return;
        }

        Diagnostics.Write(
            "Re-arming the virtual desktop notification failed twice - no more automatic " +
            "retries this session. Toggling per-desktop dimming off and on will try again.");
    }

    internal event Action? DesktopChanged;

    internal string CurrentDesktopId => current_;

    internal bool Available { get; private set; }

    internal bool Attempted { get; private set; }

    internal void Start()
    {
        Attempted = true;
        running_ = true;
        ReadOnce();

        if (Arm())
        {
            Diagnostics.Write(Available
                ? $"Per-desktop dimming on; watching the registry for changes - no polling. " +
                  $"Current desktop {current_}."
                : "Per-desktop dimming is on, but the current virtual desktop could not be read " +
                  "yet - watching the registry for the first write, no polling. All desktops " +
                  "share one dim level until then.");
            return;
        }

        Diagnostics.Write(Available
            ? $"Per-desktop dimming on, but the change notification could not be armed - " +
              $"desktop switches will not be noticed until dimming is toggled off and on again. " +
              $"Current desktop {current_}."
            : "Per-desktop dimming is on, but the current virtual desktop could not be read, " +
              "and the change notification could not be armed either - toggling dimming off " +
              "and on again will try both once more. All desktops share one dim level until then.");
    }

    internal void Stop()
    {
        Disarm();
        running_ = false;
        current_ = AllDesktops;
        Attempted = false;
        Available = false;
    }

    private bool Arm()
    {
        Disarm();

        try
        {
            foreach (var path in DesktopKeyPaths())
            {
                var key = Registry.CurrentUser.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                var signal = new ManualResetEvent(false);

                var result = Win32.RegNotifyChangeKeyValue(
                    key.Handle,
                    watchSubtree: false,
                    Win32.REG_NOTIFY_CHANGE_LAST_SET,
                    signal.SafeWaitHandle,
                    asynchronous: true);

                if (result != 0)
                {
                    Diagnostics.Write($"RegNotifyChangeKeyValue failed ({result}) for {path}.");
                    signal.Dispose();
                    key.Dispose();
                    continue;
                }

                changeRegistration_ = ThreadPool.RegisterWaitForSingleObject(
                    signal, static (state, _) => OnSignalled(state), window_, -1, executeOnlyOnce: true);

                watchedKey_ = key;
                changeSignal_ = signal;
                return true;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not watch the virtual desktop key: {ex.Message}");
        }

        return false;
    }

    private static void OnSignalled(object? state)
    {
        if (state is IntPtr window && window != IntPtr.Zero)
        {
            Win32.PostMessageW(window, Win32.WM_DESKTOP_CHANGED, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Disarm()
    {
        changeRegistration_?.Unregister(null);
        changeRegistration_ = null;

        changeSignal_?.Dispose();
        changeSignal_ = null;

        watchedKey_?.Dispose();
        watchedKey_ = null;
    }

    private void ReadOnce()
    {
        var latest = ReadCurrentDesktop();

        if (latest is null)
        {
            Available = false;
            return;
        }

        Available = true;

        if (!string.Equals(latest, current_, StringComparison.OrdinalIgnoreCase))
        {
            Diagnostics.Write($"Virtual desktop changed: {current_} -> {latest}");
            current_ = latest;
            DesktopChanged?.Invoke();
        }
    }

    private static string[] DesktopKeyPaths()
    {
        var sessionId = GetSessionId();

        return new[]
        {
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{sessionId}\VirtualDesktops",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops",
        };
    }

    internal static IReadOnlyList<string> AllDesktopIds()
    {
        foreach (var path in DesktopKeyPaths())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue(AllDesktopsValueName) is not byte[] blob ||
                    blob.Length == 0 || blob.Length % GuidByteLength != 0)
                {
                    continue;
                }

                var ids = new List<string>(blob.Length / GuidByteLength);
                for (var offset = 0; offset < blob.Length; offset += GuidByteLength)
                {
                    ids.Add(new Guid(blob.AsSpan(offset, GuidByteLength)).ToString());
                }

                return ids;
            }
            catch (Exception ex)
            {
                Diagnostics.Write($@"Reading VirtualDesktopIDs from HKCU\{path} failed: {ex.Message}");
            }
        }

        return Array.Empty<string>();
    }

    private static string? ReadCurrentDesktop()
    {
        foreach (var path in DesktopKeyPaths())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue(ValueName) is byte[] raw && raw.Length == 16)
                {
                    return new Guid(raw).ToString();
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static int GetSessionId()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        Stop();
    }
}
