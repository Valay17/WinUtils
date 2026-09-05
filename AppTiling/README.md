<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/AppTiling.svg" alt="App Tiling icon" align="center" width="120" height="120">
    <summary><h1><p>App Tiling</p></h1></summary>
  </ul>
</div>

#
### Summary

A startup-only window organizer. It waits for two specific applications to have visible windows, tiles them side by side on the primary monitor's work area, and exits immediately. No background process, no tray icon, nothing left running once the job is done.

#
### Features

- **Runs once, then exits:** Not a persistent process; verifiable in Task Manager the moment tiling completes.
- **Waits for its targets:** Polls for both applications up to a configurable timeout, and proceeds with whatever it found rather than treating "only one showed up" as failure.
- **Frame-inset correction:** Modern Windows' windows carry an invisible resize border; window positions are adjusted so adjacent windows' visible edges actually meet, not leave a gap.
- **Configurable split:** The width ratio between the two windows is a config value, not a hardcoded threshold.
- **DPI-boundary aware:** On a mixed-DPI multi-monitor setup, a window whose frame border would otherwise cross onto a lower-DPI neighboring monitor gets its placement capped to stay on the primary monitor's side of that boundary, rather than being silently resized by Windows.
- **Requires elevation:** Task Manager and Open Hardware Monitor both commonly run elevated themselves, and Windows blocks a non-elevated process from resizing or moving a window that belongs to a higher-integrity process. App Tiling has to run elevated to be able to tile them at all; it logs its own elevation state up front, since a silent no-op from a missing "Run as administrator" is otherwise hard to tell apart from the targets simply not being found.

#
### Why I made my own

I wanted Task Manager and a hardware monitor sitting tiled side by side automatically at every login, without doing it by hand every time or running a general-purpose window-tiling manager in the background for one narrow, startup-only job.

#
### Footprint

Binary size: 1.8 MB. It includes the statically-bundled native runtime AOT produces, which is what lets the exe run on a machine with nothing else installed. A framework-dependent build (relying on a separately installed .NET runtime instead of bundling one) would be smaller, but would then need .NET pre-installed on whatever machine it runs on, which defeats the point of a portable, install-free utility.

App Tiling exits in a few seconds, too fast for the long multi-hour measurement run the other utilities have (that run only captures processes still running partway into the session). A single spot-check on a test machine showed working set around 9 MB right after launch, before it tiles and exits. There is no idle CPU, handle, thread, page-fault, or I/O figure to report since nothing stays resident long enough to sample. By design, the only file activity is reading `config.ini` once at startup and, if `logging.on` is present, writing `AppTiling.log`. GPU usage doesn't apply at all here: this utility draws nothing itself, it only repositions windows owned by other processes via `SetWindowPos`, so there's no rendering of any kind to attribute a GPU cost to.

#
### Built with

C# / .NET 8, `net8.0-windows`, published as native AOT (not `SelfContained` and `PublishSingleFile` separately; AOT implies both), Win32 interop (`EnumWindows`, `GetWindowThreadProcessId`, `DwmGetWindowAttribute`). No NuGet dependencies.

```
dotnet publish -c Release
```

#
### Usage

`AppTiling.exe` does not self-register to run at logon. Add it to Task Scheduler (trigger: "At logon", action configured to "Run with highest privileges", since it needs to run elevated) or a shortcut in `shell:startup`. Task Scheduler is the one worth using if you want to avoid a UAC prompt on every login: a task set to run with highest privileges launches elevated silently, while an elevated `shell:startup` shortcut still prompts every time. On each run it looks for Task Manager and Open Hardware Monitor, tiles whichever of the two it finds, and exits. Logging is off by default; create a file named `logging.on` beside the executable to enable `AppTiling.log`.

`config.ini` is created beside the executable the first time it runs, with these two sections:

```ini
[layout]
; Percentage of the screen width given to the first target application.
; Target application names: Program.cs:17-18.
; 33 means a 33/67 split.
; Range 10-90.
firstSlotPercent=33

[wait]
; How long to wait at startup for both windows to appear before tiling whatever is there.
; This returns the instant both are found, so this only costs anything when one of them is not running.
; Seconds, range 1-3600.
timeoutSeconds=60

; How often to re-check while waiting.
; Milliseconds, range 100-10000.
pollIntervalMs=500
```

Edit any value and re-run; out-of-range values are clamped rather than rejected, and the clamp is logged.

#
### Mechanisms

Target windows are found by polling `Process.GetProcessesByName` at a configurable interval, then `EnumWindows` filtered to the process's own main visible window. Once found (or the timeout is hit), each window is restored from a minimized state if needed, then positioned with `SetWindowPos` against the primary monitor's work area. Because `SetWindowPos` targets a window's outer, frame-inclusive rect rather than its visible content, the invisible resize border is measured via `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` and compensated for, so the visible edges of the two windows meet cleanly instead of leaving a gap.

**Polling, not event-driven, and that's deliberate:** Every other utility in this repository waits on a Windows event and does nothing between them. App Tiling is the one accepted exception: there's no Windows notification for "a specific named process now has a visible window", and the whole run is over in well under a minute regardless, since it polls at most up to a configurable timeout and then exits either way. Building or maintaining an event-based mechanism for that would cost more setup than the entire task's lifetime, so a plain poll loop is the cheaper, simpler choice here specifically because the process is short-lived and one-shot, not because polling is fine in general.

**Elevation, and why it's required, not optional:** Task Manager and Open Hardware Monitor both commonly run elevated, and Windows enforces UIPI (User Interface Privilege Isolation): a lower-integrity process can enumerate and read an elevated window, but manipulating it, which is exactly what `SetWindowPos` does, requires matching elevation. App Tiling has to run elevated itself to be able to tile either target once it's running elevated, which is the common case.

An earlier design also moved the two windows to a specific virtual desktop. That was dropped: `IVirtualDesktopManager::MoveWindowToDesktop` only moves windows owned by the calling process, and the undocumented internal interface that could do it changes its binary layout across Windows builds. Not something worth depending on for a cosmetic step.

#
### Architecture

```
Startup (Task Scheduler / shell:startup)
            |
            v
  Poll for both target processes (up to timeout)
            |
            v
   For each found: restore + measure frame inset
            |
            v
     SetWindowPos (tiled, primary monitor)
            |
            v
           Exit
```

#
### Known limitations

- **A window adjacent to a lower-DPI monitor can land a few pixels short of the true screen edge**, on a mixed-DPI multi-monitor setup with the monitors in Extend mode (the adjacency check is purely geometric, based on actual monitor positions, so it can't trigger in Duplicate mode or with only one monitor). Every window carries an invisible resize border, and when that border's edge crosses onto a neighboring monitor running a different DPI, Windows substitutes its own suggested size rather than honoring the requested one. The fix here keeps the window's outer rect on the primary monitor's own side of that boundary, which costs exactly the width of the invisible border on the one affected edge; every other topology has no cost at all. Alternatives were checked and ruled out (`SetWindowPlacement`, `DeferWindowPos`, stripping the window's resize border directly), and this isn't unique to this project: a Windows tiling tool (`altdrag`) hit the identical wall, and Microsoft's own PowerToys FancyZones uses the same measure-and-compensate technique rather than eliminating the border.
- **If the target app is running elevated and App Tiling isn't**, `SetWindowPos`/`ShowWindow` silently no-op. Elevation state is logged up front specifically so this is diagnosable rather than a silent failure.
- **If neither target app is running**, nothing is arranged, it's logged, and the process exits with a non-success code, so anything checking the exit code (a script, Task Scheduler's own history) can tell the run didn't do anything.

#
### Customization locations

- **Config defaults:** `Config.cs:17,20,22`; see *Usage* for the `config.ini` content and value ranges.
- **Target application names:** `Program.cs:17-18`.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
