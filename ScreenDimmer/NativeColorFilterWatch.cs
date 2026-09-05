using Microsoft.Win32;

namespace ScreenDimmer;

internal sealed class NativeColorFilterWatch : IDisposable
{
    private const string KeyPath = @"Software\Microsoft\ColorFiltering";
    private const string ActiveValueName = "Active";
    private const string FilterTypeValueName = "FilterType";

    // FilterType values that invert luminance: 1 = Invert, 2 = Grayscale Inverted.
    // 0 and 3-5 shift color, not brightness.
    private static readonly HashSet<int> InvertingFilterTypes = new() { 1, 2 };

    private readonly IntPtr window_;

    private RegistryKey? watchedKey_;
    private ManualResetEvent? changeSignal_;
    private RegisteredWaitHandle? changeRegistration_;
    private bool disposed_;

    internal NativeColorFilterWatch(IntPtr window)
    {
        window_ = window;
    }

    internal event Action<bool>? InvertingChanged;

    internal void Start()
    {
        var inverting = ReadIsInverting();
        InvertingChanged?.Invoke(inverting);

        if (Arm())
        {
            Diagnostics.Write(
                $"Watching HKCU\\{KeyPath} for Windows' own color filter - no polling. " +
                $"Currently {(inverting ? "inverting" : "not inverting")}.");
            return;
        }

        Diagnostics.Write(
            "Could not watch Windows' native color filter for changes - overlays will not " +
            "compensate for Ctrl+Win+C inversion (Color Invert Window's own inversion is unaffected by this).");
    }

    internal void OnKeyChanged()
    {
        var inverting = ReadIsInverting();

        if (!Arm())
        {
            Diagnostics.Write("Re-arming the native color filter watch failed - further changes will not be noticed.");
        }

        InvertingChanged?.Invoke(inverting);
    }

    private bool ReadIsInverting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null)
            {
                return false;
            }

            if (key.GetValue(ActiveValueName) is not int active || active == 0)
            {
                return false;
            }

            return key.GetValue(FilterTypeValueName) is int filterType && InvertingFilterTypes.Contains(filterType);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not read the native color filter state: {ex.Message}");
            return false;
        }
    }

    private bool Arm()
    {
        Disarm();

        try
        {
            var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null)
            {
                return false;
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
                Diagnostics.Write($"RegNotifyChangeKeyValue failed ({result}) for {KeyPath}.");
                signal.Dispose();
                key.Dispose();
                return false;
            }

            changeRegistration_ = ThreadPool.RegisterWaitForSingleObject(
                signal, static (state, _) => OnSignalled(state), window_, -1, executeOnlyOnce: true);

            watchedKey_ = key;
            changeSignal_ = signal;
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not watch the native color filter key: {ex.Message}");
            return false;
        }
    }

    private static void OnSignalled(object? state)
    {
        if (state is IntPtr window && window != IntPtr.Zero)
        {
            Win32.PostMessageW(window, Win32.WM_NATIVE_COLOR_FILTER_CHANGED, IntPtr.Zero, IntPtr.Zero);
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

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        Disarm();
    }
}
