using Microsoft.Win32;

namespace ScreenDimmer;

internal sealed class TrayController : IDisposable
{
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ScreenDimmer";

    private static readonly int[] Presets = { 0, 15, 30, 45, 60, 75, 90 };

    private readonly TrayIcon icon_;
    private readonly IntPtr owner_;
    private readonly OverlayManager overlays_;
    private readonly Config config_;
    private readonly VDTracker desktops_;
    private readonly BrightnessDetector brightness_;
    private readonly Action requestRestartDesktopTracking_;
    private readonly Action requestExit_;
    private readonly CustomValuePopup customValuePopup_;

    private bool showingMenu_;
    private bool disposed_;

    private PopupMenu? activeMenu_;

    internal TrayController(
        IntPtr owner,
        Config config,
        OverlayManager overlays,
        VDTracker desktops,
        BrightnessDetector brightness,
        Action requestRestartDesktopTracking,
        Action requestExit)
    {
        owner_ = owner;
        config_ = config;
        overlays_ = overlays;
        desktops_ = desktops;
        brightness_ = brightness;
        requestRestartDesktopTracking_ = requestRestartDesktopTracking;
        requestExit_ = requestExit;

        icon_ = new TrayIcon(owner);
        icon_.SetDim(overlays_.PeakDim());
        icon_.Show();

        customValuePopup_ = new CustomValuePopup();

        overlays_.Changed += UpdateVisuals;
        UpdateVisuals();
    }

    internal void RestoreIcon()
    {
        try
        {
            icon_.Show();
            Diagnostics.Write("Re-registered the tray icon after Explorer restarted.");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not re-register the tray icon: {ex.Message}");
        }
    }

    internal void ShowMenu()
    {
        if (showingMenu_)
        {
            return;
        }

        showingMenu_ = true;
        try
        {
            BuildAndShowMenu();
        }
        finally
        {
            showingMenu_ = false;
        }
    }

    private void BuildAndShowMenu()
    {
        using var menu = new PopupMenu();

        activeMenu_ = menu;
        try
        {
        var monitorId = overlays_.MonitorIdUnderCursor();
        var currentDim = overlays_.GetDimUnderCursor();

        menu.Add($"Current Dim: {currentDim * 100:F0}%   (Max {config_.MaximumDim * 100:F0}%)");
        menu.Add($"Darkest Screen: {overlays_.PeakDimAnywhere() * 100:F0}%");
        menu.AddSeparator();

        menu.AddCustomTrigger("&Custom…", rect =>
        {
            if (monitorId is { } id)
            {
                OpenCustomValue("Current Monitor", currentDim, rect, value => overlays_.SetDim(id, value));
            }
        });

        foreach (var preset in Presets)
        {
            var value = preset / 100.0;
            menu.Add(
                $"{preset}%",
                () =>
                {
                    if (monitorId is not null)
                    {
                        overlays_.SetDim(monitorId, value);
                    }
                },
                @checked: Math.Abs(currentDim - value) < 0.005);
        }

        menu.AddSeparator();

        var all = menu.AddSubmenu("Set &Value For All");
        all.AddCustomTrigger("&Custom…", rect => OpenCustomValue("All Monitors", currentDim, rect, overlays_.SetAll));
        foreach (var preset in Presets)
        {
            var value = preset / 100.0;
            all.Add($"{preset}%", () => overlays_.SetAll(value));
        }

        var others = Monitors.All().Where(m => !m.IsPrimary).ToList();
        if (others.Count > 0)
        {
            var perMonitor = menu.AddSubmenu("Set Value For &Monitor");
            foreach (var monitor in others)
            {
                var monitorSubmenu = perMonitor.AddSubmenu(MonitorLabel(monitor.DeviceName));
                var monitorLabelText = MonitorLabel(monitor.DeviceName).Replace("&", string.Empty);
                monitorSubmenu.AddCustomTrigger(
                    "&Custom…",
                    rect => OpenCustomValue(monitorLabelText, overlays_.GetDim(monitor.DeviceName), rect,
                        value => overlays_.SetDim(monitor.DeviceName, value)));
                foreach (var preset in Presets)
                {
                    var value = preset / 100.0;
                    monitorSubmenu.Add($"{preset}%", () => overlays_.SetDim(monitor.DeviceName, value));
                }
            }
        }

        menu.AddSeparator();

        menu.Add(
            "&Separate Level Per Monitor",
            () =>
            {
                config_.PerMonitor = !config_.PerMonitor;

                overlays_.RekeyMonitorAxis(config_.PerMonitor);
                overlays_.Apply();
                UpdateVisuals();
                Diagnostics.Write($"Per-monitor dimming {(config_.PerMonitor ? "on" : "off")}.");
            },
            @checked: config_.PerMonitor);

        menu.Add(
            "Separate Level Per Virtual &Desktop",
            () =>
            {
                config_.PerVirtualDesktop = !config_.PerVirtualDesktop;

                overlays_.RekeyDesktopAxis(config_.PerVirtualDesktop);

                requestRestartDesktopTracking_();
                overlays_.Apply();
                UpdateVisuals();
            },
            @checked: config_.PerVirtualDesktop);

        menu.AddSeparator();

        menu.Add("C&lear Current", overlays_.ClearCurrentDesktop, enabled: overlays_.AnyDimmedHere());
        menu.Add("Clear &All", overlays_.ClearAll, enabled: overlays_.AnyDimmedAnywhere());

        menu.Add(
            "Start With &Windows",
            () => SetStartupEnabled(!IsStartupEnabled()),
            @checked: IsStartupEnabled());

        menu.AddSeparator();
        menu.Add("E&xit", requestExit_);

        var anchor = icon_.Anchor();
        if (anchor is null && Win32.GetCursorPos(out var cursor))
        {
            anchor = cursor;
        }

        Diagnostics.Write(
            $"menu opened; anchored at {(anchor is { } a ? $"({a.X},{a.Y})" : "unknown")}");

        if (anchor is { } point)
        {
            menu.ShowAndDispatch(owner_, point.X, point.Y);
        }
        }
        finally
        {
            activeMenu_ = null;
        }
    }

    internal void OnInitMenuPopup(IntPtr openingMenu)
    {
        if (activeMenu_ is null || !activeMenu_.TryGetCustomTrigger(openingMenu, out _))
        {
            return;
        }

        Win32.PostMessageW(owner_, Win32.WM_CUSTOM_TRIGGER_READY, openingMenu, IntPtr.Zero);
    }

    internal void OnCustomTriggerReady(IntPtr openingMenu)
    {
        if (activeMenu_ is null || !activeMenu_.TryGetCustomTrigger(openingMenu, out var onOpen) || onOpen is null)
        {
            return;
        }

        if (!Win32.GetMenuItemRect(owner_, openingMenu, 0, out var rect))
        {
            Diagnostics.Write("OnCustomTriggerReady: GetMenuItemRect failed - not opening the popup.");
            return;
        }

        var gutterWidth = Win32.GetSystemMetrics(Win32.SM_CXMENUCHECK);
        var ownerDpi = Win32.GetDpiForWindow(owner_);
        Diagnostics.Write($"OnCustomTriggerReady: rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}), " +
            $"SM_CXMENUCHECK={gutterWidth}, owner DPI={ownerDpi}.");

        onOpen(rect);
    }

    private void OpenCustomValue(string label, double current, in Win32.RECT itemRect, Action<double> apply)
    {
        var currentPercent = (int)Math.Round(current * 100);
        customValuePopup_.Show(label, currentPercent, itemRect, percent => apply(percent / 100.0));
    }

    internal void RefreshVisuals() => UpdateVisuals();

    private void UpdateVisuals()
    {
        var peak = overlays_.PeakDim();
        icon_.SetDim(peak);

        var current = overlays_.GetDimUnderCursor();
        var darkest = overlays_.PeakDimAnywhere();

        string tooltip;
        if (darkest <= 0.001 && current <= 0.001)
        {
            tooltip = "Screen Dimmer - OFF";
        }
        else if (Math.Abs(darkest - current) < 0.005)
        {
            tooltip = $"Screen Dimmer - {current * 100:F0}%";
        }
        else
        {
            tooltip = $"Screen Dimmer - {current * 100:F0}% here, {darkest * 100:F0}% darkest";
        }

        if (config_.PerVirtualDesktop && desktops_.Attempted && !desktops_.Available)
        {
            tooltip = "Screen Dimmer - desktop tracking unavailable";
        }

        icon_.SetTooltip(tooltip);
    }

    private static string MonitorLabel(string deviceName)
    {
        const string prefix = @"\\.\DISPLAY";
        if (deviceName.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(deviceName.AsSpan(prefix.Length), out var number))
        {
            return $"Monitor &{number}";
        }

        return deviceName;
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
        overlays_.Changed -= UpdateVisuals;
        icon_.Dispose();
        customValuePopup_.Dispose();
    }
}
