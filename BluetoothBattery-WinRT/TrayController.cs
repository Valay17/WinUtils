using Microsoft.Win32;

namespace BluetoothBattery;

// The tray icon, its menu, and the device popup - the entire user interface.
// The menu is rebuilt every time it opens rather than built once and patched,
// so its live state (e.g. whether the radio is on) can never go stale.
internal sealed partial class TrayController : IDisposable
{
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "BluetoothBattery";

    private readonly IntPtr window_;
    private readonly BluetoothMonitor monitor_;
    private readonly TrayIcon icon_;
    private readonly DevicePopup popup_;
    private readonly Action requestExit_;

    // Guards against overlapping tray activations - Windows sends
    // NIN_KEYSELECT twice for a single Enter on some shells.
    private bool handlingActivation_;

    private bool rendered_;
    private int renderedLowest_ = -1;
    private string renderedTooltip_ = string.Empty;

    private bool disposed_;

    internal TrayController(
        IntPtr window, BluetoothMonitor monitor, Action requestExit)
    {
        window_ = window;
        monitor_ = monitor;
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

    internal void OnThemeChanged() => popup_.DiscardPaintResources();

    // -----------------------------------------------------------------------
    // Tray activation
    // -----------------------------------------------------------------------

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

    // Shows the popup and refreshes it; clicking the icon never closes it -
    // dismissal is click-away and Escape, handled by the popup itself.
    private void ShowOrRefreshPopup()
    {
        using var _timing = Timing.Measure("ShowOrRefreshPopup");

        var devices = monitor_.Snapshot();

        var willRefresh = monitor_.RadioIsOn && devices.Any(d => d.IsConnected);

        popup_.ShowDevices(devices, monitor_.RadioState, willRefresh);

        if (!willRefresh)
        {
            return;
        }

        _ = RefreshForPopupAsync();
    }

    // Deliberately not awaited by the caller - the popup is already on
    // screen and the numbers arrive when they arrive.
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

    // -----------------------------------------------------------------------
    // Menu
    // -----------------------------------------------------------------------

    private void ShowMenu()
    {
        using var _timing = Timing.Measure("ShowMenu");

        // The popup and the menu must never be up together - both take the
        // foreground and dismiss on deactivation, so with both open each
        // waits on the other.
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

        // Anchored to the icon rather than the cursor, so opening from the
        // keyboard never moves the pointer.
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

            // On a worker thread: this runs two full SetupAPI sweeps before
            // writing the file, and doing that on the UI thread blocks the
            // message loop for seconds.
            await Task.Run(() => Diagnostics.WriteDeviceDump(raw, known));

            Diagnostics.Write($"Diagnostics written to {Diagnostics.DumpPath}");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Write Diagnostics failed: {ex}");
        }
    }

    // -----------------------------------------------------------------------
    // Visuals
    // -----------------------------------------------------------------------

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

        // Nothing visible changed, so nothing is sent to the shell. `lowest`
        // and `tooltip` together are the whole of what this method can put
        // on screen, so comparing just these two is safe, not a guess.
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

        // TrayIcon truncates to the shell's own 127-character limit.
        return string.Join(", ", reported.Select(d => $"{d.Name} - {d.BatteryText}"));
    }

    // -----------------------------------------------------------------------
    // Start with Windows
    // -----------------------------------------------------------------------

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
                // Environment.ProcessPath, not Assembly.Location: the latter
                // is empty in a single-file published (and AOT-compiled)
                // binary.
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
