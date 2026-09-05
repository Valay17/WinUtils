using Microsoft.Win32;

namespace BluetoothBattery;

internal sealed partial class TrayController : IDisposable
{
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private const string StartupValueName = "BluetoothBattery";

    private readonly IntPtr window_;
    private readonly BluetoothMonitor monitor_;
    private readonly TrayIcon icon_;
    private readonly DevicePopup popup_;
    private readonly UiDispatcher dispatcher_;
    private readonly Action requestExit_;

    private bool handlingActivation_;

    private bool rendered_;
    private int renderedLowest_ = -1;
    private string renderedTooltip_ = string.Empty;

    private bool disposed_;

    internal TrayController(
        IntPtr window, BluetoothMonitor monitor, UiDispatcher dispatcher,
        Action requestExit)
    {
        window_ = window;
        monitor_ = monitor;
        dispatcher_ = dispatcher;
        requestExit_ = requestExit;

        icon_ = new TrayIcon(window);
        popup_ = new DevicePopup();

        popup_.AnchorTo(icon_.AnchorRect);

        monitor_.DevicesChanged += OnDevicesChanged;

        UpdateVisuals();
        icon_.Show();
    }

    internal void RestoreIcon()
    {
        Diagnostics.Write("Explorer restarted - re-adding the tray icon.");
        icon_.Show();
    }

    // Posted through the dispatcher - SystemEvents fires this on its own worker
    // thread, and DiscardPaintResources touches popup state the UI thread may be mid-paint reading.
    internal void OnThemeChanged() => dispatcher_.Post(popup_.DiscardPaintResources);

    internal void OnTrayActivated(bool leftClick)
    {
        if (handlingActivation_)
        {
            return;
        }

        handlingActivation_ = true;

        try
        {
            if (leftClick)
            {
                ShowOrRefreshPopup();
            }
            else
            {
                ShowMenu();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Handling a tray click failed: {ex}");
        }
        finally
        {
            handlingActivation_ = false;
        }
    }

    private void ShowOrRefreshPopup()
    {
        using var _timing = Timing.Measure("ShowOrRefreshPopup");

        _ = monitor_.CatchUpAsync();

        var devices = monitor_.Snapshot();

        var willRefresh = monitor_.RadioIsOn && devices.Any(d => d.IsConnected);

        popup_.ShowDevices(devices, monitor_.RadioState, willRefresh);

        if (!willRefresh)
        {
            return;
        }

        _ = RefreshForPopupAsync();
    }

    private async Task RefreshForPopupAsync()
    {
        using var _timing = Timing.Measure("RefreshForPopupAsync");

        try
        {
            await monitor_.RefreshAsync();
            UpdateVisuals();

            if (popup_.Visible)
            {
                popup_.ShowDevices(monitor_.Snapshot(), monitor_.RadioState, refreshing: false);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Refreshing for the popup failed: {ex}");
        }
    }

    private void ShowMenu()
    {
        using var _timing = Timing.Measure("ShowMenu");

        // The popup and the menu must never be up together - both take the
        // foreground and dismiss on deactivation.
        popup_.Hide();

        using var menu = new PopupMenu();

        menu.Add("Start With &Windows",
                 () => SetStartupEnabled(!IsStartupEnabled()),
                 @checked: IsStartupEnabled());

        if (Diagnostics.DiagnosticsAvailable)
        {
            menu.AddSeparator();
            menu.Add("Write &Diagnostics", () => _ = WriteDiagnosticsAsync());
        }

        menu.AddSeparator();
        menu.Add("E&xit", requestExit_);

        var anchor = icon_.Anchor();

        if (anchor is { } point)
        {
            menu.ShowAndDispatch(
                window_, point.X, point.Y, Win32.TPM_BOTTOMALIGN | Win32.TPM_LEFTALIGN);
        }
        else if (Win32.GetCursorPos(out var cursor))
        {
            menu.ShowAndDispatch(
                window_, cursor.X, cursor.Y, Win32.TPM_BOTTOMALIGN | Win32.TPM_LEFTALIGN);
        }
    }

    private async Task WriteDiagnosticsAsync()
    {
        try
        {
            var raw = await BluetoothMonitor.EnumerateRawAsync();
            var known = monitor_.Snapshot();

            // Off the UI thread - WriteDeviceDump runs two full SetupAPI sweeps and
            // writes a file, which would otherwise block the message loop.
            await Task.Run(() => Diagnostics.WriteDeviceDump(raw, known));

            Diagnostics.Write($"Diagnostics written to {Diagnostics.DumpPath}");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Write Diagnostics failed: {ex}");
        }
    }

    private void OnDevicesChanged()
    {
        UpdateVisuals();

        if (popup_.Visible)
        {
            popup_.ShowDevices(monitor_.Snapshot(), monitor_.RadioState, refreshing: false);
        }
    }


    private void UpdateVisuals()
    {
        using var _timing = Timing.Measure("UpdateVisuals");

        var devices = monitor_.Snapshot();

        var lowest = devices
            .Select(d => d.BatteryPercent)
            .Where(p => p is not null)
            .Select(p => p!.Value)
            .DefaultIfEmpty(-1)
            .Min();

        var tooltip = BuildTooltip(devices);

        // Skip the rebuild if nothing visible would actually change - lowest and
        // tooltip together are the whole of what this method puts on screen.
        if (rendered_ && lowest == renderedLowest_ && tooltip == renderedTooltip_)
        {
            return;
        }

        rendered_ = true;
        renderedLowest_ = lowest;
        renderedTooltip_ = tooltip;

        icon_.SetIcon(BatteryIcon.Create(monitor_.RadioState, devices.Count > 0, lowest >= 0 ? lowest : null));
        icon_.SetTooltip(tooltip);
    }

    private string BuildTooltip(IReadOnlyList<DeviceView> devices)
    {
        if (!monitor_.RadioIsOn)
        {
            return "Bluetooth - OFF";
        }

        if (devices.Count == 0)
        {
            return "No Bluetooth Device Connected";
        }

        var reported = devices.Where(d => d.BatteryPercent.HasValue).ToList();
        if (reported.Count == 0)
        {
            return $"{devices.Count} device(s), no battery reported";
        }

        return string.Join(", ", reported.Select(d => $"{d.Name} - {d.BatteryText}"));
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey);
            return key?.GetValue(StartupValueName) is not null;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not read the startup registry value: {ex.Message}");
            return false;
        }
    }

    private static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                // Environment.ProcessPath, not Assembly.Location: the latter is empty
                // in a single-file published or ahead-of-time compiled binary.
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    Diagnostics.Write("Could not determine this executable's path; startup not enabled.");
                    return;
                }

                key.SetValue(StartupValueName, $"\"{path}\"");
                Diagnostics.Write($"Enabled start with Windows: {path}");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                Diagnostics.Write("Disabled start with Windows.");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not update the startup registry value: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        monitor_.DevicesChanged -= OnDevicesChanged;

        popup_.Dispose();
        icon_.Dispose();
    }
}
