<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/MonitorBrightness.svg" alt="Monitor Brightness icon" align="center" width="120" height="120">
    <summary><h1><p>Monitor Brightness</p></h1></summary>
  </ul>
</div>

#
### Summary

Controls the brightness of external monitors over DDC/CI, from a tray icon. Windows' own brightness slider only controls the laptop's internal panel; this handles the external monitors it doesn't reach.

#
### Features

- **Live slider popup:** A draggable trackbar per controllable monitor, opened with a left-click; the value updates live while dragging and writes to the monitor only once, on release.
- **Preset menu:** Right-click for per-monitor and all-monitors presets (0/25/50/75/100%).
- **Keyboard-operable:** The slider popup is fully usable without a mouse: arrow keys adjust the focused row, Tab/Up/Down move between monitors, Ctrl+arrow snaps to the nearest 5%.
- **Auto re-detection:** Plugging or unplugging a monitor, or changing resolution, triggers a fresh enumeration automatically. A manual "Re-detect Monitors" entry covers the one case that can't be observed: toggling DDC/CI on inside a monitor's own on-screen menu.
- **Internal panel shown, not hidden:** Listed as "Internal Display", grayed, so it doesn't look like a display went missing from the list.

#
### Why I made my own

Windows has no native brightness control for external monitors at all. Its own slider only reaches the internal laptop panel. The alternative is digging into each monitor's on-screen-display menu by hand every time, which I didn't want to do. I wanted one brightness control that covers every external monitor individually, works from the keyboard as well as the mouse, and doesn't need a vendor's own bundled software running in the background just to change a slider.

#
### Footprint

Binary size: 223 KB.

Measured on a single test machine. Treat these as illustrative, not a guarantee for other hardware.

| Metric | Idle | Max | Mode |
|---|---|---|---|
| CPU (one core) | 0.00% | 0.31% | 0.00% |
| Private working set | 1.16 MB | 1.75 MB | 1.70 MB |
| Handles | 207 | 207 | 207 |
| Threads | 2 | 5 | 2 |
| Page faults/sec | 0.00 | 25.32 | 0.00 |

This utility does no file I/O at all beyond reading `config.ini` once at startup. There's no runtime write path at all: the hotkey and step values are read once and never saved back. Page faults are not a slower version of that: they're the OS mapping a virtual-memory page into the process's working set, mostly resolved from the executable image already sitting in the file-system cache rather than a fresh disk read, which is why the rate spikes during activity instead of holding constant. GPU usage isn't listed either: this utility draws nothing beyond a tray icon and the occasional popup, both handed to DWM (Desktop Window Manager, the OS component that draws every window to the screen) for compositing like any other window, and PDH (Performance Data Helper, the Windows API these counters come from) itself returned invalid data for GPU Engine counters in this project's testing regardless.

#
### Built with

C++23, native Win32, no framework, built via MSVC and CMake, static CRT. Links `dxva2`, `user32`, `gdi32`, `shell32`, `comctl32`, `dbghelp` directly. No installer, no runtime dependency.

```
cmake -B build -S .
cmake --build build --config Release
```

#
### Usage

Run `MonitorBrightness.exe`. Left-click the tray icon for the live slider popup, right-click for the preset menu. A monitor that can't report a brightness level over DDC/CI, including the internal panel, stays listed but grayed, labeled "(no DDC/CI)".

`config.ini` is never created automatically; hand-create it beside the executable to override the compiled-in defaults (Ctrl+Alt+PageUp/ PageDown, 10 points per keypress, hotkeys disabled unless built with `-DMONITORBRIGHTNESS_ENABLE_HOTKEYS=1`):

```ini
[hotkey]
upModifiers=3
upVk=33
downModifiers=3
downVk=34

[behavior]
step=10
```

`upModifiers`/`downModifiers` and `upVk`/`downVk` are raw Win32 values (`MOD_CONTROL | MOD_ALT` is 3, `VK_PRIOR`/Page Up is 33, `VK_NEXT`/Page Down is 34); look up the constants for a different combination in `<winuser.h>`. `step` is clamped 1-50. Restart the app after editing, no rebuild needed.

Treat `config.ini` as the way to try combinations without rebuilding. Once you've found the ones you actually want to keep, hardcode them instead: see *Customization Locations* for the compiled-in defaults, which are what a fresh copy with no `config.ini` present will actually use.

#
### Mechanisms

Every monitor Windows reports is enumerated (`EnumDisplayMonitors`/`GetPhysicalMonitorsFromHMONITOR`), and each is asked directly whether it can report a brightness level, which decides whether it's controllable. The internal laptop panel is identified via `QueryDisplayConfig`'s per-path output-technology field rather than inferred from "no DDC/CI" (an external monitor with DDC/CI switched off in its own menu would look identical) or "is the Windows-primary monitor" (a user can set an external display as primary). The slider popup writes to the monitor only on release or a discrete keyboard step, never mid-drag. The live label updates continuously, but the DDC/CI write itself is a slow round trip over the display cable and only needs to happen once per gesture. Keyboard-driven hotkeys for brightness up/down exist in the code but ship disabled by default, since they were never actually requested as a feature. They're a compile-time option, not just hidden behind a runtime flag.

#
### Architecture

```
EnumDisplayMonitors
        |
        v
Per monitor: GetMonitorBrightness (probe)
        |
        v
 Controllable? --no--> listed, grayed
        | yes
        v
   Tray icon (left-click: slider popup, right-click: preset menu)
        |
        v
  SetMonitorBrightness (on release / keyboard step only)
```

#
### Known limitations

- **The per-monitor submenu arrow points right even though the submenu opens left:** This only shows up when the menu is near the right edge of the screen, where Windows' own edge-avoidance flips a submenu to open leftward instead of the default rightward; the arrow glyph doesn't follow. This is normal Win32 menu behavior, not specific to this app: a classic (non-owner-drawn) menu ties arrow direction to text alignment as one flag, and getting the arrow right would also force-mirror every item's text. Not worth an owner-drawn menu rewrite for a cosmetic arrow glyph.
- **The slider accent color is fixed**, not read from the user's Windows accent color. Deferred, not built yet.
- **The root context menu can visibly close while the live slider popup (`Set Value For All`) is being dragged by mouse:** `WS_EX_NOACTIVATE` is set and does its documented job, but `WM_ACTIVATE` still fires on the popup regardless, an admitted gap in that style's guarantee. Cosmetic only: the popup keeps working through its own low-level input hooks regardless of whether the menu is still visibly open. Keyboard-driven interaction never hits this path at all.
- **Hovering over the slider popup can still open a sibling preset submenu underneath it:** `TrackPopupMenu` takes mouse capture through a privileged, OS-level flag that ordinary code can't compete with or override. Cosmetic only: clicks never leak regardless, and a drag already in progress is unaffected.
- **A one-off HDMI-unplug crash**, not reproduced since and not actively chased without a reproduction. The crash-dump writer stays armed and will capture it if it starts happening with any frequency.

#
### Customization locations

- **Hotkey bindings and step size (compile-time defaults):** `Config.h:23-24,26` (`brightnessUp`/`brightnessDown`, `step`). Hotkeys are compiled out entirely unless the build is configured with `-DMONITORBRIGHTNESS_ENABLE_HOTKEYS=1` (`Config.h:7-8`). See *Usage* for the `config.ini` format to override these without a rebuild.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
