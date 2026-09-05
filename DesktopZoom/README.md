<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/DesktopZoom.png" alt="Desktop Zoom icon" align="center" width="120" height="120">
    <summary><h1><p>Desktop Zoom</p></h1></summary>
  </ul>
</div>

#
### Summary

Keyboard step-zoom for the whole screen, with the view following the cursor continuously once zoomed in. Mechanically similar to KDE Plasma's built-in zoom effect: hotkey-driven, cursor-centered, and cheap to run because it lives inside the compositor rather than capturing frames.

#
### Features

- **Three always-on hotkeys:** Zoom in, zoom out, reset to 1.0x. No on/off mode to toggle first. Registered with Windows via `RegisterHotKey`, not polling or watching every keystroke; Windows itself only delivers an event when one of these three exact combinations is pressed.
- **Cursor-following view:** Once zoomed past 1.0x, the visible region tracks the pointer continuously, not just at the moment of the last keypress.
- **Zero idle cost:** The pointer-tracking hook is only installed while actually zoomed. At 1.0x the process does nothing at all.
- **Multi-monitor correct:** The zoom origin is computed in virtual-screen coordinates spanning every monitor, not just the primary one, so the cursor-follow stays seamless crossing between monitors too.
- **Always resets on startup/exit/crash:** A crash mid-session can't leave the screen stuck zoomed in.

#
### Why I made my own

KDE Plasma has had a compositor-level zoom effect with this exact cursor-following behavior for a long time. Windows' own Magnifier exists but doesn't follow the cursor the same way and comes with its own window chrome. This reproduces that mechanism directly on the Magnification API, as a tray-only utility with no visible UI of its own.

#
### Footprint

Binary size: 173 KB.

Measured on a single test machine. Treat these as illustrative, not a guarantee for other hardware.

| Metric | Idle | Max | Mode |
|---|---|---|---|
| CPU (one core) | 0.00% | 0.00% | 0.00% |
| Private working set | 1.51 MB | 1.75 MB | 1.72 MB |
| Handles | 220 | 220 | 220 |
| Threads | 3 | 5 | 3 |
| Page faults/sec | 0.00 | 8.17 | 0.00 |

This utility does no file I/O at all during normal operation. It doesn't even read a config file, since none exists by design; the only filesystem touch is a one-time check at startup for a stray leftover `config.ini` from an older version, removed if found. Page faults are not a slower version of that: they're the OS mapping a virtual-memory page into the process's working set, mostly resolved from the executable image already sitting in the file-system cache rather than a fresh disk read, which is why the rate spikes during activity instead of holding constant. GPU usage isn't listed either: the zoom transform itself runs inside DWM's (Desktop Window Manager, the OS component that draws every window to the screen) own composition pass (see *Mechanisms*), not this process's, so any GPU cost belongs to `dwm.exe`'s own accounting. PDH (Performance Data Helper, the Windows API these counters come from) itself also returned invalid data for GPU Engine counters in this project's testing regardless.

#
### Built with

C++23, MSVC, CMake, static CRT, single executable. No installer, no runtime dependency.

```
cmake -B build -S .
cmake --build build --config Release
```

#
### Usage

Run `DesktopZoom.exe`. `Ctrl+Alt+=` zooms in one step, `Ctrl+Alt+-` zooms out, `Ctrl+Alt+0` resets to 1.0x. Holding a key down zooms smoothly via Windows' own key-repeat. The tray tooltip always shows the current zoom level. There is no config file; every tunable is a compile-time constant, so changing one means editing `Settings.h` and rebuilding.

#
### Mechanisms

Zooming itself is one API call, `MagSetFullscreenTransform`, applied inside the composition pass with no capture loop and no per-frame cost. While zoomed, the pointer is tracked via `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` filtered to the cursor object, installed only for the duration of the zoom. The view's origin is computed from the pointer position with `origin = pointer * (1 - 1/level)`, linear across the whole screen and never needing a clamp, which keeps the pointer at the same relative position in the zoomed view as on the unzoomed screen. That's what makes the image track smoothly instead of lurching. Redundant transforms are skipped by comparing against the last applied origin, since at higher zoom levels a large pointer movement maps to a much smaller change in view origin.

#
### Architecture

```
Hotkey (zoom in/out/reset)
        |
        v
MagSetFullscreenTransform(level, origin)
        |
        v
 level > 1.0? --no--> pointer hook removed, idle
        | yes
        v
SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, cursor)
        |
        v
  origin = pointer * (1 - 1/level)  -->  re-apply transform
```

#
### Known limitations

- **If the hotkeys collide with something else already registered on a given machine**, that combination is logged as taken rather than silently doing nothing. Rebinding means editing `Settings.h` and rebuilding; there's no runtime fallback or config file.

#
### Customization locations

Everything tunable is `Settings.h`'s `settings` namespace, in full. There is no config file by design; edit these constants directly and rebuild:

```cpp
constexpr float kZoomStep = 0.25f;
constexpr float kMaxZoom = 8.0f;

constexpr UINT kHotkeyModifiers = MOD_CONTROL | MOD_ALT;  // Ctrl+Alt
constexpr UINT kZoomInVk = VK_OEM_PLUS;                   // =
constexpr UINT kZoomOutVk = VK_OEM_MINUS;                 // -
constexpr UINT kResetVk = '0';                            // 0
```

- **Zoom step and ceiling:** Lines 10-11 above (`kZoomStep`, `kMaxZoom`).
- **Hotkey bindings:** Lines 13-16 above (`kHotkeyModifiers` applies to all three; `kZoomInVk`, `kZoomOutVk`, `kResetVk` are the individual keys).

There is no config file by design; every value above is a compile-time constant, so a rebind means editing the header and rebuilding.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
