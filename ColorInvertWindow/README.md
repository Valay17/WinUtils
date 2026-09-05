<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/ColorInvertWindow.png" alt="Color Invert Window icon" align="center" width="120" height="120">
    <summary><h1><p>Color Invert Window</p></h1></summary>
  </ul>
</div>

#
### Summary

A hotkey-driven, per-application screen color inverter for Windows. Mark a window once and the whole screen inverts whenever that window has focus, using the Magnification API instead of a per-window overlay, so there is no standing cost while nothing is inverted.

#
### Features

- **Hotkey marking:** Win+C by default, marks the current window; the same hotkey on an already-marked window removes the mark.
- **Focus-following, not overlay-based:** Inversion state is purely a function of which window is in front/focus/foreground/active right now all driven by a Windows event hook; not a window this app draws and has to keep positioned.
- **Two mark tiers:** Session marks (by window handle, gone on restart) and `[always]` rules (`marks.ini`, survive restart, name an executable that should always invert, e.g. Task Manager).
- **Zero standing cost:** No overlay window, no per-frame redraw, no polling of any kind. The process does nothing between focus changes.

#
### Why I made my own

Windows' built-in Color Filters accessibility feature does something similar, but it is global and keyboard-shortcut-only, with no way to scope it to specific applications. KDE Plasma gets true per-window inversion essentially for free, since KWin's invert effect is a shader inside the compositor's own render pass. Windows' DWM (Desktop Window Manager, the OS component that draws every window to the screen) has no public equivalent. Getting an app-scoped, hotkey-toggled inversion on Windows without either DLL-injecting the target process or maintaining a per-frame capture-and-redraw overlay meant writing this instead.

#
### Footprint

Binary size: 195 KB.

Measured on a single test machine. Treat these as illustrative, not a guarantee for other hardware.

| Metric | Idle | Max | Mode |
|---|---|---|---|
| CPU (one core) | 0.00% | 0.94% | 0.00% |
| Private working set | 1.02 MB | 1.21 MB | 1.18 MB |
| Handles | 202 | 202 | 202 |
| Threads | 2 | 4 | 2 |
| Page faults/sec | 0.00 | 7.38 | 0.00 |

This utility does no file I/O at all beyond reading `config.ini` and `marks.ini` once at startup. Nothing writes them back during normal operation: session marks live in memory only, and toggling an `[always]` rule off just suspends it in memory, it never touches the file. Page faults are not a slower version of that: they're the OS mapping a virtual-memory page into the process's working set, mostly resolved from the executable image already sitting in the file-system cache rather than a fresh disk read, which is why the rate spikes during activity instead of holding constant. GPU usage isn't listed either: the inversion effect itself runs inside DWM's own composition pass (see *Mechanisms*), not this process's, so any GPU cost belongs to `dwm.exe`'s own accounting. PDH (Performance Data Helper, the Windows API these counters come from) itself also returned invalid data for GPU Engine counters in this project's testing regardless.

#
### Built with

C++23, Win32 API directly (Magnification API, `SetWinEventHook`, `RegisterHotKey`), built via MSVC and CMake, static CRT. No installer, no runtime dependency, no telemetry.

```
cmake -B build -S .
cmake --build build --config Release
```

#
### Usage

Run `ColorInvertWindow.exe`. Focus a window and press `Win+C` to mark it; the screen inverts whenever that window has focus, and stops when focus moves elsewhere. Press the same hotkey again on an already-marked window to remove the mark.

`marks.ini`, created beside the executable the first time it runs, holds the `[always]` rules that stay inverted permanently, with this default content:

```ini
[always]
1=taskmgr.exe
2=openhardwaremonitor.exe
```

Add a line under `[always]` to mark another application permanently (`3=notepad.exe`, for instance); for the shared-host-process case described in Known Limitations, use the `<executable>|<title>` form on its own line instead. Hand-edit and save, no rebuild or restart needed.

The two default entries are seeded once and tracked separately: right after the file is created, a `[meta]` section (`defaultsWritten=1`) gets written to it too. Delete `taskmgr.exe`/`openhardwaremonitor.exe` from `[always]` and they stay gone; that flag is what stops them from being re-added on the next launch.

`config.ini` is different: it is never created automatically. If you want to rebind the hotkey, create it yourself beside the executable:

```ini
[hotkey]
modifiers=8
vk=67
```

`modifiers` and `vk` are raw Win32 values (`MOD_WIN` is 8, `'C'` as a virtual-key code is 67); look up the constants you want in `<winuser.h>` or a virtual-key table online. A missing or malformed `config.ini` silently falls back to Win+C rather than erroring.

Treat `config.ini` as the way to try combinations without rebuilding. Once you've found the one you actually want to keep, hardcode it instead: see *Customization Locations* for the compiled-in default, which is what a fresh copy with no `config.ini` present will actually use.

#
### Mechanisms

Inversion itself is one API call, `MagSetFullscreenColorEffect`, which runs inside the composition pass and costs nothing per frame. There is no capture loop and no timer. Focus changes are observed via `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, which fires only when the foreground window actually changes (a handful of events per minute in normal use), delivered through the calling thread's own message loop rather than a dedicated hook thread. Transient shell UI (the Alt+Tab switcher, desktop-transition frames) is recognized by window class and ignored, so it can't be mistaken for the user switching applications. Because a virtual-desktop switch also changes the foreground window, no separate desktop-tracking code exists; the same hook covers it for free.

Inversion is screen-wide while a marked window has focus, not clipped to that window's bounds. There is no public compositor API on Windows that allows true per-window scoping without either injecting the target process or repainting a capture every frame, and both were rejected: a capture-and-redraw approach means a standing per-frame GPU cost for as long as anything is inverted, and DLL injection gets flagged by antivirus and anti-cheat software, refused by protected processes, and takes the host application down with it if the injected hook ever faults.

#
### Architecture

```
Focus changes (SetWinEventHook)
            |
            v
   Is the new foreground window marked?
      |                        |
      no                      yes
      |                        |
      v                        v
 leave inversion as-is   MagSetFullscreenColorEffect(on)
```

#
### Known limitations

- **Screen-wide, not clipped to the window:** Inversion covers the whole screen while a marked window has focus, not just that window's bounds. Windows has no public compositor API for true per-window scoping without either injecting the target process or repainting a capture every frame, and both were rejected. Accepted tradeoff.
- **Every monitor inverts, not just the one the marked window is on:** This isn't the app being multi-monitor aware; it's the opposite. It makes no distinction between monitors at all, because the Windows API behind it doesn't either: `MagSetFullscreenColorEffect` takes no monitor parameter, and the fullscreen surface it applies to already spans every monitor once a second one is in Extend mode. There's no per-monitor variant of this API to switch to. Whether that reads as a bug or a feature depends on what you wanted, but it costs nothing extra regardless, since it's the same single API call no matter how many monitors are attached.
- **A single visible frame shows the inversion switching state:** Not a limitation, just an unavoidable, expected consequence of a full-screen effect toggling instantly rather than fading in.
- **An `[always]` rule matches by executable alone when only one rule names it, so every window of that executable gets inverted, not just one:** This is expected, often the whole point of marking something like `notepad.exe`: every Notepad window inverts. The title only comes into play when two separate rules name the *same* executable, which happens with apps that share a host process (for example - Calculator and Sticky Notes both run under `ApplicationFrameHost.exe`); the title is the only way to tell those two rules apart. In that specific case, two windows with an identical title can't be distinguished, since a title is the only identity that survives a reboot. Designed like this cause it met my requirements
- **On rare occasions, virtual desktop tracking at startup is a bit off:** It corrects itself as soon as you switch to another desktop.

#
### Customization locations

- **Default hotkey (compile-time):** `Config.h:9-10` (`modifiers`, `vk`). Overridable without a rebuild via `config.ini`; see *Usage* for the exact format.
- **Seeded `[always]` rules and the `marks.ini` template text:** `Marks.cpp:305-306,354-380`; see *Usage* for the file content.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
