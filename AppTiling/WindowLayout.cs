using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppTiling;

internal static unsafe class WindowLayout
{
    // Above this many pixels, a placement miss is treated as a real overshoot
    // rather than ordinary sub-pixel rounding.
    private const int OvershootTolerancePx = 50;

    // Fallback until Configure() reads the real value from config.ini.
    private static double _firstSlotFraction = 0.33;

    internal static void Configure(int firstSlotPercent)
    {
        _firstSlotFraction = firstSlotPercent / 100.0;
    }

    private const int SW_RESTORE = 9;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
    }

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    private static string ReadFixed(char* buffer, int capacity)
    {
        var length = 0;
        while (length < capacity && buffer[length] != '\0')
        {
            length++;
        }

        return new string(buffer, 0, length);
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        public fixed char dmDeviceName[32];
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        public fixed char dmFormName[32];

        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public int StateFlags;
        public fixed char DeviceID[128];
        public fixed char DeviceKey[128];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr hMon, out int pScale);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    // EDID physical size lives at offsets 21/22 of the base block; 0 means not specified.
    private static (int WidthCm, int HeightCm)? TryGetEdidPhysicalSizeCm(string monitorDeviceId)
    {
        try
        {
            var parts = monitorDeviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "MONITOR")
            {
                return null;
            }

            var registryPath = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
            using var key = Registry.LocalMachine.OpenSubKey(registryPath);
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 23)
            {
                return null;
            }

            var widthCm = edid[21];
            var heightCm = edid[22];
            return (widthCm, heightCm);
        }
        catch
        {
            return null;
        }
    }

    private readonly struct MonitorTopologyEntry
    {
        public RECT Full { get; init; }
        public RECT Work { get; init; }
        public uint DpiX { get; init; }
        public bool IsPrimary { get; init; }
    }

    private static List<MonitorTopologyEntry> LogMonitorTopology()
    {
        var index = 0;
        var entries = new List<MonitorTopologyEntry>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT rect, IntPtr _) =>
        {
            index++;
            var infoEx = new MONITORINFOEX { cbSize = sizeof(MONITORINFOEX) };
            var gotInfo = GetMonitorInfoEx(hMonitor, ref infoEx);
            var dpiResult = GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out var dpiX, out var dpiY);
            var isPrimary = gotInfo && (infoEx.dwFlags & 0x1) != 0; // MONITORINFOF_PRIMARY

            Log.Write($"Monitor {index}{(isPrimary ? " (primary)" : "")}: full {rect.Width}x{rect.Height} " +
                      $"at ({rect.Left},{rect.Top}), work {(gotInfo ? $"{infoEx.rcWork.Width}x{infoEx.rcWork.Height} at ({infoEx.rcWork.Left},{infoEx.rcWork.Top})" : "unknown")}, " +
                      $"DPI {(dpiResult == 0 ? $"{dpiX}x{dpiY}" : "unknown")}");

            if (gotInfo)
            {
                entries.Add(new MonitorTopologyEntry
                {
                    Full = rect, Work = infoEx.rcWork,
                    DpiX = dpiResult == 0 ? dpiX : 96,
                    IsPrimary = isPrimary,
                });

                // Gated behind Log.Enabled: skips the EDID registry read and extra
                // Win32 calls when nobody will read this diagnostic data.
                if (Log.Enabled)
                {
                    var deviceName = ReadFixed(infoEx.szDevice, 32);
                    var scalePercent = dpiResult == 0 ? (int)Math.Round(dpiX * 100.0 / 96.0) : -1;

                    var devmode = new DEVMODE { dmSize = (short)sizeof(DEVMODE) };
                    var gotMode = EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref devmode);

                    var recommendedScale = -1;
                    if (GetScaleFactorForMonitor(hMonitor, out var scaleFromApi) == 0)
                    {
                        recommendedScale = scaleFromApi;
                    }

                    var friendlyName = "unknown";
                    var edidNote = "not available";
                    var deviceInfo = new DISPLAY_DEVICE { cb = sizeof(DISPLAY_DEVICE) };
                    if (EnumDisplayDevicesW(deviceName, 0, ref deviceInfo, 0))
                    {
                        var deviceString = ReadFixed(deviceInfo.DeviceString, 128);
                        friendlyName = string.IsNullOrEmpty(deviceString) ? "unknown" : deviceString;

                        var deviceId = ReadFixed(deviceInfo.DeviceID, 128);
                        var edid = TryGetEdidPhysicalSizeCm(deviceId);
                        if (edid is { } size)
                        {
                            edidNote = size.WidthCm == 0 && size.HeightCm == 0
                                ? "monitor did not report it (EDID bytes are 0)"
                                : $"{size.WidthCm}cm x {size.HeightCm}cm";
                        }
                    }

                    Log.Write($"Monitor {index}: device {deviceName}, \"{friendlyName}\", " +
                              $"native {(gotMode ? $"{devmode.dmPelsWidth}x{devmode.dmPelsHeight}" : "unknown")}, " +
                              $"scale {(scalePercent > 0 ? $"{scalePercent}%" : "unknown")} " +
                              $"(Windows recommends {(recommendedScale > 0 ? $"{recommendedScale}%" : "unknown")}), " +
                              $"EDID physical size {edidNote}.");
                }
            }

            return true;
        }, IntPtr.Zero);

        if (Log.Enabled)
        {
            var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            var awarenessName = awareness switch
            {
                0 => "Unaware", 1 => "System", 2 => "PerMonitor(V2)", _ => $"unknown ({awareness})",
            };
            Log.Write($"This process' own DPI awareness right now: {awarenessName}.");
            Log.Write($"GetSystemMetrics (classic, not DPI-aware by monitor): " +
                      $"primary {GetSystemMetrics(SM_CXSCREEN)}x{GetSystemMetrics(SM_CYSCREEN)}, " +
                      $"virtual desktop {GetSystemMetrics(SM_CXVIRTUALSCREEN)}x{GetSystemMetrics(SM_CYVIRTUALSCREEN)} " +
                      $"at ({GetSystemMetrics(SM_XVIRTUALSCREEN)},{GetSystemMetrics(SM_YVIRTUALSCREEN)}).");
        }

        return entries;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_TOPMOST = 0x00040000;

    private static void AnnounceResult(string detail)
    {
        if (!Log.Enabled)
        {
            return;
        }

        MessageBoxW(
            IntPtr.Zero,
            $"{detail}\n\nLook at Task Manager now, then click OK.",
            "App Tiling",
            MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);

    // DWMWA_EXTENDED_FRAME_BOUNDS - the window's visible frame.
    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // Distance from the window's outer rect (what SetWindowPos operates on) to its
    // visible drawn edges. Modern frames carry an invisible resize border on the
    // left, right and bottom, none on top.
    private readonly struct FrameInset
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }

        public static FrameInset None => default;
    }

    private static FrameInset MeasureFrameInset(IntPtr hwnd, string label)
    {
        if (!GetWindowRect(hwnd, out var outer))
        {
            return FrameInset.None;
        }

        Log.Write($"{label}: current size before this placement is " +
                  $"({outer.Left},{outer.Top}) {outer.Width}x{outer.Height}.");

        var size = Marshal.SizeOf<RECT>();
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, size) != 0)
        {
            // Pre-DWM or composition disabled - nothing to compensate for.
            return FrameInset.None;
        }

        var inset = new FrameInset
        {
            Left = visible.Left - outer.Left,
            Top = visible.Top - outer.Top,
            Right = outer.Right - visible.Right,
            Bottom = outer.Bottom - visible.Bottom,
        };

        // Negative or implausibly large values mean something unexpected; ignoring
        // them is safer than moving a window somewhere wrong.
        if (inset.Left < 0 || inset.Top < 0 || inset.Right < 0 || inset.Bottom < 0 ||
            inset.Left > 32 || inset.Top > 32 || inset.Right > 32 || inset.Bottom > 32)
        {
            Log.Write($"{label}: ignoring implausible frame inset " +
                      $"({inset.Left},{inset.Top},{inset.Right},{inset.Bottom}).");
            return FrameInset.None;
        }

        if (inset.Left != 0 || inset.Top != 0 || inset.Right != 0 || inset.Bottom != 0)
        {
            Log.Write($"{label}: invisible frame border L{inset.Left} T{inset.Top} " +
                      $"R{inset.Right} B{inset.Bottom} - compensating.");
        }

        return inset;
    }

    internal static bool EnsureRestored(IntPtr hwnd, string label)
    {
        var minimized = IsIconic(hwnd);
        var maximized = IsZoomed(hwnd);

        if (!minimized && !maximized)
        {
            return false;
        }

        Log.Write($"{label} is {(minimized ? "minimized" : "maximized")} - restoring before layout");
        if (!ShowWindow(hwnd, SW_RESTORE))
        {
            Log.Write($"WARNING: restoring {label} returned false " +
                      $"(Win32 error {Marshal.GetLastWin32Error()})");
        }

        return true;
    }

    internal static bool Arrange(IReadOnlyList<(IntPtr Hwnd, string Label, int Slot)> windows, int slotCount)
    {
        if (windows.Count == 0)
        {
            return true;
        }

        var topology = LogMonitorTopology();

        foreach (var (hwnd, label, _) in windows)
        {
            Log.Write($"{label}: DPI before placement {GetDpiForWindow(hwnd)}.");
        }

        if (!TryGetPrimaryWorkArea(windows[0].Hwnd, out var work, out var primaryMonitorRect))
        {
            Log.Write("ERROR: could not determine the primary monitor work area - skipping layout.");
            return false;
        }

        Log.Write($"Primary work area: {work.Width}x{work.Height} at ({work.Left},{work.Top})");

        if (slotCount <= 1)
        {
            var singlePlaced = true;
            foreach (var (hwnd, label, slot) in windows)
            {
                if (slot != 0)
                {
                    Log.Write($"ERROR: {label} has slot {slot}, outside the 1 available - skipping it.");
                    singlePlaced = false;
                    continue;
                }

                singlePlaced &= Place(hwnd, label, work, out _);
            }

            return singlePlaced;
        }

        var primaryMonitor = topology.FirstOrDefault(m => m.IsPrimary);

        var dpiMismatchDetected = false;
        var adjacentOnRight = false;
        var adjacentOnLeft = false;

        if (topology.Count > 1 && primaryMonitor.DpiX > 0)
        {
            var otherMonitors = topology.Where(m => !m.IsPrimary).ToList();
            var lowerDpiMonitors = otherMonitors.Where(m => m.DpiX < primaryMonitor.DpiX).ToList();
            dpiMismatchDetected = lowerDpiMonitors.Count > 0;

            if (dpiMismatchDetected)
            {
                // FirstOrDefault's "not found" is a zeroed struct, so DpiX > 0 means found.
                var rightAdjacentMonitor = lowerDpiMonitors.FirstOrDefault(m =>
                    m.Full.Left == primaryMonitorRect.Right && VerticalRangesOverlap(primaryMonitorRect, m.Full));
                adjacentOnRight = rightAdjacentMonitor.DpiX > 0;

                var leftAdjacentMonitor = lowerDpiMonitors.FirstOrDefault(m =>
                    m.Full.Right == primaryMonitorRect.Left && VerticalRangesOverlap(primaryMonitorRect, m.Full));
                adjacentOnLeft = leftAdjacentMonitor.DpiX > 0;

                var relevant = adjacentOnRight ? rightAdjacentMonitor
                    : adjacentOnLeft ? leftAdjacentMonitor
                    : lowerDpiMonitors[0];
                var ratio = (double)primaryMonitor.DpiX / relevant.DpiX;
                Log.Write($"DPI-mismatch topology detected: {lowerDpiMonitors.Count} lower-DPI monitor(s) present, " +
                          $"most relevant DPI {relevant.DpiX} < primary {primaryMonitor.DpiX} (ratio {ratio:F4}) - " +
                          $"genuinely adjacent to the primary's " +
                          $"{(adjacentOnRight ? "right" : adjacentOnLeft ? "left" : "neither left nor right")} " +
                          $"edge (that monitor sits at ({relevant.Full.Left},{relevant.Full.Top})).");
            }
        }

        if (!dpiMismatchDetected)
        {
            Log.Write("DPI-mismatch topology not detected " +
                      $"({topology.Count} monitor(s), primary DPI {primaryMonitor.DpiX}) - placing directly " +
                      "with nothing to correct for.");
        }

        // Built for exactly two slots - Open Hardware Monitor and Task Manager, in
        // that order. A third slot would need a real redesign of this method.
        IntPtr slot0Hwnd = IntPtr.Zero, slot1Hwnd = IntPtr.Zero;
        var slot0Label = string.Empty;
        var slot1Label = string.Empty;
        var slot0Present = false;
        var slot1Present = false;
        var placedCount = 0;

        foreach (var (hwnd, label, slot) in windows)
        {
            switch (slot)
            {
                case 0:
                    slot0Hwnd = hwnd; slot0Label = label; slot0Present = true; placedCount++;
                    break;
                case 1:
                    slot1Hwnd = hwnd; slot1Label = label; slot1Present = true; placedCount++;
                    break;
                default:
                    Log.Write($"ERROR: {label} has slot {slot}, outside the 2 available - skipping it.");
                    break;
            }
        }

        var plannedFirstWidth = (int)Math.Round(work.Width * _firstSlotFraction);

        var allPlaced = true;

        var slot1ActualLeft = work.Left + plannedFirstWidth;
        var slot1RequestedMatched = true;
        var slot0RequestedMatched = true;
        var slot0Retried = false;

        if (slot1Present)
        {
            var plannedSlot1 = new RECT
            {
                Left = work.Left + plannedFirstWidth, Top = work.Top,
                Right = work.Right, Bottom = work.Bottom,
            };

            var slot1RequestTarget = plannedSlot1;
            FrameInset? slot1KnownInset = null;

            if (adjacentOnRight)
            {
                var inset = MeasureFrameInset(slot1Hwnd, slot1Label);
                slot1KnownInset = inset;
                // Cap width so the window's outer frame stays inside the primary
                // monitor's real right edge, avoiding the DPI-boundary substitution.
                var dpiBoundarySafeWidth = primaryMonitorRect.Right - inset.Right - plannedSlot1.Left;

                slot1RequestTarget = new RECT
                {
                    Left = plannedSlot1.Left, Top = plannedSlot1.Top,
                    Right = plannedSlot1.Left + dpiBoundarySafeWidth, Bottom = plannedSlot1.Bottom,
                };

                Log.Write($"DpiBoundaryFormula: the secondary monitor is genuinely adjacent to the primary's " +
                          $"right edge at {primaryMonitorRect.Right} - capping {slot1Label} at " +
                          $"{dpiBoundarySafeWidth}px (this window's own {inset.Right}px right-side frame border) " +
                          $"instead of the planned {plannedSlot1.Width}px, one shot, trusted directly.");
            }
            else if (dpiMismatchDetected)
            {
                Log.Write($"DpiBoundaryFormula: DPI-mismatch topology detected, but the secondary monitor isn't " +
                          $"adjacent to {slot1Label}'s right edge - no cap needed, placing the full planned " +
                          $"{plannedSlot1.Width}px directly, one shot.");
            }

            allPlaced &= Place(slot1Hwnd, slot1Label, slot1RequestTarget, out var actualSlot1, slot1KnownInset);
            Log.Write($"{slot1Label}: DPI after placement {GetDpiForWindow(slot1Hwnd)}.");

            if (topology.Count <= 1)
            {
                Log.Write($"{slot1Label}: single-monitor topology ({topology.Count} monitor(s)) - trusting the " +
                          "plan directly, no match check, no corrective retry or bisection attempted regardless " +
                          "of what actually landed.");
            }

            if (topology.Count > 1)
            {
                slot1ActualLeft = actualSlot1.Left;
            }

            if (topology.Count > 1 && !Matches(actualSlot1, slot1RequestTarget))
            {
                slot1RequestedMatched = false;
                var observedDeltaW = actualSlot1.Width - slot1RequestTarget.Width;
                var observedDeltaH = actualSlot1.Height - slot1RequestTarget.Height;

                if (observedDeltaW > OvershootTolerancePx)
                {
                    Log.Write($"DpiBoundaryFormula: the direct placement overshot by {observedDeltaW}x{observedDeltaH}px " +
                              $"anyway (wanted {slot1RequestTarget.Width}x{slot1RequestTarget.Height}, got " +
                              $"{actualSlot1.Width}x{actualSlot1.Height}) - the geometry-based formula's " +
                              "assumption didn't hold for this topology. Accepted as-is, not corrected live.");
                }
                else
                {
                    // Within the rounding-gap range - one corrective retry using the
                    // observed delta.
                    var correctedSlot1 = new RECT
                    {
                        Left = plannedSlot1.Left, Top = plannedSlot1.Top,
                        Right = plannedSlot1.Left + Math.Max(400, slot1RequestTarget.Width - observedDeltaW),
                        Bottom = plannedSlot1.Top + Math.Max(300, slot1RequestTarget.Height - observedDeltaH),
                    };

                    Log.Write($"CorrectiveRetry: the direct request was off by {observedDeltaW}x{observedDeltaH}px " +
                              $"(wanted {slot1RequestTarget.Width}x{slot1RequestTarget.Height}, got {actualSlot1.Width}x{actualSlot1.Height}) - " +
                              $"within rounding-gap range, not a real overshoot. Retrying once, requesting " +
                              $"{correctedSlot1.Width}x{correctedSlot1.Height}.");

                    allPlaced &= Place(slot1Hwnd, slot1Label, correctedSlot1, out var actualSlot1Retry);
                    slot1ActualLeft = actualSlot1Retry.Left;
                    slot1RequestedMatched = Matches(actualSlot1Retry, slot1RequestTarget);

                    Log.Write($"CorrectiveRetry result: wanted {slot1RequestTarget.Width}x{slot1RequestTarget.Height}, " +
                              $"got {actualSlot1Retry.Width}x{actualSlot1Retry.Height} - " +
                              (slot1RequestedMatched ? "landed on it this time." : "still did not land on it."));
                }
            }

            if (!slot1RequestedMatched)
            {
                Log.Write($"{slot1Label}: real position after placing is left={slot1ActualLeft}, not the " +
                          $"requested {plannedSlot1.Left} - fitting " +
                          $"{(slot0Present ? slot0Label : "the other slot")} to match rather than re-asserting.");
            }
        }

        if (slot0Present)
        {
            var slot0Left = work.Left;
            FrameInset? slot0KnownInset = null;

            if (adjacentOnLeft)
            {
                var inset = MeasureFrameInset(slot0Hwnd, slot0Label);
                slot0KnownInset = inset;
                slot0Left = work.Left + inset.Left;

                Log.Write($"DpiBoundaryFormula: the secondary monitor is genuinely adjacent to the primary's " +
                          $"left edge at {primaryMonitorRect.Left} - shifting {slot0Label}'s left edge to " +
                          $"{slot0Left}px (this window's own {inset.Left}px left-side frame border) instead of " +
                          $"the true edge at {work.Left}, one shot, trusted directly.");
            }

            var finalSlot0 = new RECT
            {
                Left = slot0Left, Top = work.Top, Right = slot1ActualLeft, Bottom = work.Bottom,
            };

            allPlaced &= Place(slot0Hwnd, slot0Label, finalSlot0, out var actualSlot0, slot0KnownInset);
            slot0RequestedMatched = Matches(actualSlot0, finalSlot0);
            Log.Write($"{slot0Label}: DPI after placement {GetDpiForWindow(slot0Hwnd)}.");

            if (!slot0RequestedMatched)
            {
                Log.Write($"{slot0Label}: real result after placing did not match what was just requested " +
                          $"(requested ({finalSlot0.Left},{finalSlot0.Top}) {finalSlot0.Width}x{finalSlot0.Height}, " +
                          $"actual ({actualSlot0.Left},{actualSlot0.Top}) {actualSlot0.Width}x{actualSlot0.Height}) " +
                          "- retrying once, same target.");

                allPlaced &= Place(slot0Hwnd, slot0Label, finalSlot0, out var actualSlot0Retry, slot0KnownInset);
                slot0Retried = true;
                slot0RequestedMatched = Matches(actualSlot0Retry, finalSlot0);
            }
        }

        if (placedCount < slotCount)
        {
            Log.Write($"{slotCount - placedCount} slot(s) left empty for the application(s) that are not running.");
        }

        var resultDetail = dpiMismatchDetected
            ? $"DPI-mismatch topology detected; the secondary monitor is genuinely adjacent to the " +
              $"{(adjacentOnRight ? "right" : adjacentOnLeft ? "left" : "neither")} edge - see the log for " +
              "whether that meant capping a window's request, one shot, geometry-based, not a live search."
            : "topology did not match the DPI-mismatch condition - placed directly, nothing to correct for.";

        var slot0Detail = !slot0Present
            ? string.Empty
            : slot0RequestedMatched
                ? (slot0Retried
                    ? "\nOpen Hardware Monitor did not land right the first time, but the retry fixed it."
                    : "\nOpen Hardware Monitor matched what was requested.")
                : "\nOpen Hardware Monitor still did NOT match what was requested, even after a retry.";

        AnnounceResult(slot1Present
            ? $"{resultDetail}\nTask Manager's real position after placing " +
              (slot1RequestedMatched ? "matched what was requested." : "did NOT match what was requested - " +
                                                                        "Open Hardware Monitor was fitted to reality instead.") +
              slot0Detail
            : resultDetail);

        return allPlaced;
    }

    private static bool Matches(in RECT actual, in RECT planned) =>
        actual.Left == planned.Left && actual.Top == planned.Top &&
        actual.Width == planned.Width && actual.Height == planned.Height;

    private static bool VerticalRangesOverlap(in RECT a, in RECT b) => a.Top < b.Bottom && b.Top < a.Bottom;

    // actualVisible falls back to target if the re-read fails.
    private static bool Place(IntPtr hwnd, string label, RECT target, out RECT actualVisible, FrameInset? knownInset = null)
    {
        // target is the visible rect; SetWindowPos operates on the outer rect, so
        // expand by the frame inset so the drawn edges land on the boundaries.
        var inset = knownInset ?? MeasureFrameInset(hwnd, label);

        var x = target.Left - inset.Left;
        var y = target.Top - inset.Top;
        var width = target.Width + inset.Left + inset.Right;
        var height = target.Height + inset.Top + inset.Bottom;

        // NOACTIVATE: these windows are being sent to another desktop and should
        // not steal focus. NOZORDER: leave the existing stacking order untouched.
        var ok = SetWindowPos(
            hwnd, IntPtr.Zero, x, y, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);

        actualVisible = target;
        if (GetWindowRect(hwnd, out var actualOuter))
        {
            actualVisible = new RECT
            {
                Left = actualOuter.Left + inset.Left,
                Top = actualOuter.Top + inset.Top,
                Right = actualOuter.Right - inset.Right,
                Bottom = actualOuter.Bottom - inset.Bottom,
            };
        }

        if (ok)
        {
            Log.Write($"Placed {label}: requested ({target.Left},{target.Top}) {target.Width}x{target.Height}, " +
                      $"outer ({x},{y}) {width}x{height} - actual afterward: " +
                      $"({actualVisible.Left},{actualVisible.Top}) {actualVisible.Width}x{actualVisible.Height}");
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        Log.Write($"ERROR: placing {label} failed (Win32 error {error})." +
                  (error == 5
                      ? " Access denied - that window belongs to a higher-integrity process. Run App Tiling elevated."
                      : string.Empty));
        return false;
    }

    private static bool TryGetPrimaryWorkArea(IntPtr referenceWindow, out RECT work) =>
        TryGetPrimaryWorkArea(referenceWindow, out work, out _);

    // Also returns the primary monitor's full (non-work-area) rect, needed to tell
    // whether a window's outer rect crosses onto the neighboring monitor - the
    // taskbar-shrunk work area alone can't answer that.
    private static bool TryGetPrimaryWorkArea(IntPtr referenceWindow, out RECT work, out RECT fullMonitor)
    {
        work = default;
        fullMonitor = default;

        // MONITOR_DEFAULTTOPRIMARY returns the primary monitor regardless of which
        // monitor the reference window is actually on.
        var monitor = MonitorFromWindow(referenceWindow, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        work = info.rcWork;
        fullMonitor = info.rcMonitor;
        return work.Width > 0 && work.Height > 0;
    }
}
