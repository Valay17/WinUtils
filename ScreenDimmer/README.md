<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/ScreenDimmer.svg" alt="Screen Dimmer icon" align="center" width="120" height="120">
    <summary><h1><p>Screen Dimmer</p></h1></summary>
  </ul>
</div>

#
### Summary

Dims the screen below what a monitor's own hardware brightness slider can reach, using a software overlay per monitor, controlled from the tray. Supports independent dim levels per monitor and per virtual desktop.

#
### Features

- **Per-monitor and per-virtual-desktop control:** Each axis can be tracked independently or shared, so dim levels can differ by monitor, by desktop, both, or neither.
- **Hard floor on how dark it can go:** A configurable ceiling (default 90%, clamped 10-95% regardless of what the config file says), so the screen can never go dark enough to lose the tray icon needed to undo it.
- **Click-through overlay:** The dim layer never intercepts mouse input; everything beneath it works normally.
- **Aware of the rest of the suite, passively:** Notices if Color Invert Window exits without cleaning up, and if Windows' own accessibility Color Filters gets toggled, purely to react correctly. It never touches either of those itself.

#
### Why I made my own

Every monitor eventually hits a hardware brightness floor that's still too bright for a dark room, and there wasn't an existing lightweight tool that went past that floor with independent control per monitor and per virtual desktop.

#
### Footprint

Binary size: 3.0 MB (down from roughly 155 MB before the WinForms-free rewrite, see "Built with" below). It includes the statically-bundled native runtime AOT produces, which is what lets the exe run on a machine with nothing else installed. A framework-dependent build (relying on a separately installed .NET runtime instead of bundling one) would be smaller, but would then need .NET pre-installed on whatever machine it runs on, which defeats the point of a portable, install-free utility.

Measured on a single test machine. Treat these as illustrative, not a guarantee for other hardware.

| Metric | Idle | Max | Mode |
|---|---|---|---|
| CPU (one core) | 0.00% | 0.94% | 0.00% |
| Private working set | 3.19 MB | 4.94 MB | 4.68 MB |
| Handles | 227 | 244 | 237 |
| Threads | 2 | 5 | 4 |
| Page faults/sec | 0.00 | 156.73 | 0.00 |

There's no idle file I/O here: `config.json` is only written when a dim level actually changes through the tray menu, a user action, never on a timer or in a loop. Page faults are not a slower version of that: they're the OS mapping a virtual-memory page into the process's working set, mostly resolved from the executable image already sitting in the file-system cache rather than a fresh disk read, which is why the rate spikes during activity (the 156.73 max above lines up with UI interaction, not a steady background rate) instead of holding constant. GPU usage isn't listed either: the dim overlay is a layered window composited by DWM (Desktop Window Manager, the OS component that draws every window to the screen), not rendered by this process's own GPU work, so any GPU cost belongs to `dwm.exe`'s own accounting. PDH (Performance Data Helper, the Windows API these counters come from) itself also returned invalid data for GPU Engine counters in this project's testing regardless.

#
### Built with

C# / .NET 8, `net8.0-windows`, published as native AOT, direct Win32 interop, no UI framework. No `System.Windows.Forms` and no `System.Drawing` (those become NuGet packages once WinForms is gone, and this project takes none). An earlier WinForms-based build existed and was fully replaced; going framework-free cut the published size from roughly 155 MB down to about 3 MB, since AOT and WinForms cannot be used together.

```
dotnet publish -c Release
```

#
### Usage

Run `ScreenDimmer.exe`. Right-click the tray icon for presets, per-monitor control, and the two axis toggles ("Separate Level Per Monitor" / "Separate Level Per Virtual Desktop"). Dimming is controlled entirely through the tray; the hardware brightness keys are not wired to it (see *Known Limitations*).

`config.json` doesn't exist until you actually change a dim level for the first time; nothing is written on startup. Once it exists, it looks like this (two monitors, one dimmed, per-monitor tracking on):

```json
{
  "maxDim": 0.9,
  "perMonitor": true,
  "perVirtualDesktop": false,
  "stepPercent": 5,
  "dimLevels": [
    { "monitorId": "\\\\.\\DISPLAY1", "desktopId": "*", "dim": 0.35 },
    { "monitorId": "\\\\.\\DISPLAY2", "desktopId": "*", "dim": 0.0 }
  ]
}
```

`desktopId` is `"*"` (every virtual desktop shares this monitor's level) unless per-virtual-desktop tracking is on, in which case each desktop gets its own entry with a desktop GUID in place of the wildcard. The file is rewritten automatically every time a dim level changes through the tray; hand-editing it works too, but isn't needed for normal use.

#
### Mechanisms

Each monitor gets its own layered, click-through overlay window (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST`), with opacity mapped linearly from the stored dim level. Dim state is keyed by `(monitorId, desktopId)`, where either half of the key can be a wildcard meaning "all", so the two axis toggles are just swapping one half of that key between a specific id and the wildcard, never averaging or blending values together. Virtual-desktop tracking is entirely poll-free: it watches the registry value Explorer itself writes on a desktop switch via `RegNotifyChangeKeyValue`, rather than polling for a change.

#
### Architecture

```
Brightness-down at 0% (internal panel)
            |
            v
   Software dim increases one step
            |
            v
Per-(monitor, desktop) dim level looked up
            |
            v
  Per-monitor overlay opacity updated (WS_EX_LAYERED)
```

#
### Known limitations

- **The root context menu can visibly close while the live slider popup (`Set Value For All`) is being dragged by mouse:** `WS_EX_NOACTIVATE` is set and does its documented job, but `WM_ACTIVATE` still fires on the popup regardless, an admitted gap in that style's guarantee. Cosmetic only: the popup keeps working through its own low-level input hooks regardless of whether the menu is still visibly open. Keyboard-driven interaction never hits this path at all.
- **Submenu arrows point right even though every submenu here opens left:** This only shows up when the menu is near the right edge of the screen, where Windows' own edge-avoidance flips a submenu to open leftward instead of the default rightward; the tray icon always sits in a taskbar corner, so it happens every time here. This is normal Win32 menu behavior, not specific to this app: same arrow/text-alignment coupling as the popup issue above; not worth an owner-drawn menu rewrite for a cosmetic glyph.
- **Reaching "Custom…" by keyboard takes two Down presses, not one**, since the two grayed informational lines above it count as stops along the way. A classic Win32 popup menu's own keyboard navigation simply does not skip disabled items; there's no per-application hook to change that short of the same owner-drawn rewrite already ruled out above.
- **The hardware brightness keys don't trigger the software dim:** An earlier version of this utility tried to intercept the physical brightness-down key at 0% and hand off into the software dim automatically, using a low-level keyboard hook. That doesn't work on the hardware this was tested on: those keys are raised by ACPI/the vendor's own driver, never enter the keyboard input queue at all, and so can't be observed by any hook, confirmed directly. This isn't a bug to fix later; there's no hook-based path around it on this class of hardware. Dimming is entirely manual, through the tray, on every machine.

#
### Customization locations

- **Dim step and maximum ceiling (compile-time defaults):** `Config.cs:17,27` (`DefaultMaxDim`, `stepPercent`); the absolute ceiling clamp is `Config.cs:19,96` (`AbsoluteMaxDimCeiling`, applied in `Normalize()`), which cannot be overridden from the config file.
- **Per-monitor / per-desktop tracking defaults:** `Config.cs:21,24` (`perMonitor`, `perVirtualDesktop`). See *Usage* for the `config.json` format these end up in.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
