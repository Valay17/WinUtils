<div align="center">

<h1><p>Win Utils</p></h1>

</div>

### Summary

Win Utils is a set of six small Windows tray utilities, each covering one thing I wanted a specific tool to do without the overhead of a background service, a subscription, or a pile of features I'd never touch. Every one of them ships as a single, portable `.exe` with no installer. Five of the six stay idle until something happens (a hotkey, a device event, a window message) instead of polling in a loop; App Tiling is the one deliberate exception, and its own README explains why. The table below covers what each one does; the rest of this file covers the shared design and tooling behind all six.

#
### The utilities

| Utility | What it does | Executable | Source |
|---|---|---|---|
| Color Invert Window | Hotkey (Win+C by default) inverts the colors of the currently focused window. | `ColorInvertWindow.exe` | [`ColorInvertWindow/`](ColorInvertWindow) |
| Bluetooth Battery | Tray icon showing the battery level of a paired Bluetooth device. | `BluetoothBattery.exe` (Win32 build, actively maintained) | [`BluetoothBattery-Win32/`](BluetoothBattery-Win32), [`BluetoothBattery-WinRT/`](BluetoothBattery-WinRT) |
| App Tiling | Waits for a couple of specific apps to be running at startup, then tiles them side by side and exits. | `AppTiling.exe` | [`AppTiling/`](AppTiling) |
| Monitor Brightness | Controls external monitor brightness over DDC/CI, from the tray or a hotkey. | `MonitorBrightness.exe` | [`MonitorBrightness/`](MonitorBrightness) |
| Desktop Zoom | Keyboard step-zoom that follows the cursor, similar in mechanism to KDE Plasma's zoom effect. | `DesktopZoom.exe` | [`DesktopZoom/`](DesktopZoom) |
| Screen Dimmer | Dims the screen past your monitor's own hardware brightness floor, through a per-monitor, per-desktop overlay. | `ScreenDimmer.exe` | [`ScreenDimmer/`](ScreenDimmer) |

Bluetooth Battery ships two builds because the Win32 and WinRT Bluetooth APIs each have different limitations. Both compile and ship as exes; Win32 is the one that actually gets used and fixed going forward, WinRT is kept around for comparison. See [`BluetoothBattery-Win32/README.md`](BluetoothBattery-Win32) for why.

#
### Compatibility

Color Invert Window, Desktop Zoom, and Screen Dimmer all touch the same screen-wide Windows surfaces (the Magnification API, layered overlay windows), so cross-utility conflicts were checked and issues were resolved.

Color Invert Window and Desktop Zoom detect each other directly (each holds its own named mutex and checks for the other's) and they compose without conflict: they drive two separate, independent properties of the same underlying magnifier, so running both at once works exactly as running either alone would. Screen Dimmer detects when Color Invert Window's inversion is active and switches its own dim overlay from black to white, since a black semi-transparent overlay would otherwise brighten an inverted screen instead of dimming it.

Desktop Zoom and Screen Dimmer running together hasn't been explicitly tested. If you hit a real conflict between any of these, or with another Windows accessibility feature, please open an issue.

Everything here has only been tested on Windows 10. Windows 11 is untested, cause no one uses that garbage.

The right-click tray menu was built with dark mode in mind. There's no light/dark switch for it yet, so it can look a bit off if Windows is set to light mode. Might add one later.

#
### Design philosophy

Every utility here is event-driven, with one deliberate exception: App Tiling polls briefly at startup, since it is a short-lived, run-once task with no Windows notification to wait on instead. The other five have no idle polling loop, no timer ticking in the background doing nothing. Each one waits on a Windows signal and only does work when one actually fires, which keeps idle CPU and memory close to zero across the board.

Beyond that: no telemetry, no phoning home, and no admin rights required for five of the six - App Tiling needs to run elevated, since the two applications it tiles commonly run elevated themselves. No redistributable runtime to install separately: the C++ builds link the CRT statically, and the C# builds are published as native AOT. Every utility is a single, portable `.exe`, no installer, no background service, no registry footprint from a setup step.

Keyboard-first was one of the goals here too. As a fellow keyboard warrior, that was one of the priorities.

#
### Built with

- **C++23** (Win32 API directly, MSVC, static CRT, built via CMake): Color Invert Window, Monitor Brightness, Desktop Zoom.
- **C# / .NET 8** (native AOT, Win32 P/Invoke): App Tiling, Screen Dimmer, Bluetooth Battery (Win32 build).
- **C# / .NET 9** (native AOT, WinRT projections): Bluetooth Battery (WinRT build) — needed net9 specifically for its AOT source generator, since net8's could not compile the WinRT CCW that `Radio.GetRadiosAsync()` requires.
- No Electron, no UI framework wrapper, no installer for any of the six.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
