<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/BluetoothBattery.svg" alt="Bluetooth Battery icon" align="center" width="120" height="120">
    <summary><h1><p>Bluetooth Battery</p></h1></summary>
  </ul>
</div>

#
### Summary

A tray icon showing the battery level of a paired Bluetooth device, built directly on Win32/SetupAPI. This is the actively maintained build; an earlier WinRT-based implementation also exists in this repository (kept for comparison, no longer receiving fixes).

#
### Features

- **Matches what Windows shows:** Reads the same `DEVPKEY_Bluetooth_Battery` property Windows Settings itself reads.
- **Profile-aware:** Correctly distinguishes which Bluetooth profiles actually carry a battery value (Hands-Free/Headset) from ones that never do (A2DP/AVRCP, music-only), rather than reporting "unavailable" without explanation.
- **On-demand refresh, no background polling:** Battery values are fetched when something actually changes (a device connects, a menu opens), not on a timer.
- **BLE/GATT fallback:** A raw GATT battery-service read exists for devices that don't expose battery through the classic path, used only when the classic sources come back empty.
- **On-demand diagnostics:** A built-in report dumps exactly what this utility sees for every known device (profiles, connection state, every source checked and its result), useful for debugging a specific device's behavior without needing to read the source.

#
### Why Win32, not WinRT

This project originally shipped on WinRT (`DeviceWatcher`, `Radio.GetRadiosAsync`, `BluetoothLEDevice`/GATT). Memory captures showed WinRT initialization alone adding several megabytes to the process the instant it was touched, on top of what the tray/config/icon/popup layer needed by itself. Separate ETW tracing showed the WinRT Bluetooth DLL set costing a 44-169ms load window and several megabytes of commit activity right after process start. Classic-Bluetooth battery reads also turned out to need no WinRT at all: the WinRT `AssociationEndpoint` battery property came back empty on every device tested, while the same devices' battery worked correctly through the plain SetupAPI property both approaches ultimately reach. Given that WinRT contributed cost and no capability this rewrite couldn't get another way, replacing the discovery/event layer with Win32/SetupAPI was the straightforward call. The tray UI itself (icon, popup, menu) carried over unchanged; only the discovery and event layer underneath is different.

#
### Footprint

Binary size: 5.0 MB. It includes the statically-bundled native runtime AOT produces, which is what lets the exe run on a machine with nothing else installed. A framework-dependent build (relying on a separately installed .NET runtime instead of bundling one) would be smaller, but would then need .NET pre-installed on whatever machine it runs on, which defeats the point of a portable, install-free utility.

Measured on a single test machine. Treat these as illustrative, not a guarantee for other hardware.

| Metric | Idle | Max | Mode |
|---|---|---|---|
| CPU (one core) | 0.00% | 24.40% (device enumeration burst) | 0.00% |
| Private working set | 6.11 MB | 6.73 MB | 6.45 MB |
| Handles | 266 | 292 | 278 |
| Threads | 4 | 8 | 4 |
| Page faults/sec | 0.00 | 2847.99 (enumeration burst) | 0.00 |

This utility does no file I/O at all beyond an optional `logging.on`-gated log write. There's no config file: nothing here is currently configurable, so nothing is read or written at startup. Page faults are not a slower version of that: they're the OS mapping a virtual-memory page into the process's working set, mostly resolved from the executable image already sitting in the file-system cache rather than a fresh disk read, which is why the rate spikes during activity (the 2847.99 max above is the same device enumeration burst that drives the CPU max) instead of holding constant. GPU usage isn't listed either: this utility draws nothing beyond a tray icon and the device popup, both handed to DWM (Desktop Window Manager, the OS component that draws every window to the screen) for compositing like any other window, and PDH (Performance Data Helper, the Windows API these counters come from) itself returned invalid data for GPU Engine counters in this project's testing regardless.

#
### Built with

C# / .NET 8, `net8.0-windows`, published as native AOT, direct Win32/SetupAPI interop. SetupAPI/CfgMgr32 for device enumeration, raw COM vtable calls for radio on/off state (not `[ComImport]`/WinRT), a raw GATT client for the BLE fallback path. No NuGet dependencies, no WinRT.

```
dotnet publish -c Release
```

#
### Usage

Run `BluetoothBattery.exe`. The tray icon shows the lowest-battery connected device by default; click it for a per-device popup with individual levels. To generate a diagnostics dump for a specific device issue, create a file named `logging.on` beside the executable and check the resulting log. It lists every known device, which profiles were found for it, and (when a battery value is missing) a plain-language explanation of why. There's no config file: nothing about this utility is currently user-configurable.

#
### Mechanisms

Paired devices are enumerated via `SetupDiGetClassDevs` on the Bluetooth device class, the same broad enumeration Windows Settings itself uses. Radio on/off state is read through a raw, undocumented-but-stable in-proc COM object (the same one behind WinRT's `Radio.State`), reached via direct vtable calls rather than any COM activation machinery, not WinRT and AOT/trim-safe. Device-arrival/removal notifications (`RegisterDeviceNotification` on the Bluetooth device interface class) trigger a full re-sweep rather than tracking each device incrementally. Coarser-grained than WinRT's per-device event stream, but effectively free for the handful of paired devices a machine has. When a device has no battery value, the diagnostics explanation distinguishes a a disconnected device from one that is connected but has no service nodes exposing battery yet, rather than collapsing both into the same message.

#
### Architecture

```
Device change notification (arrival/removal)
            |
            v
      Full device re-sweep (SetupAPI)
            |
            v
  Per device: classic battery property, then container-id
  fallback, then BLE/GATT fallback if still empty
            |
            v
        Tray icon + popup updated
```

#
### Known limitations

- **Classic Bluetooth battery only works through the Hands-Free/Headset profile:** A2DP and AVRCP (music playback and track controls) carry no battery capability at all, which is a Bluetooth profile boundary, not something this utility can work around. See "Matches what Windows shows" above; if Windows Settings can't show a battery level for a device either, neither can this.
- **The BLE/GATT fallback path is unverified against hardware:** It was written to the documented API shape but never exercised, since every device tested so far resolves through the classic path first.
- **Might not work consistently on every device.** One test case showed a battery level in Windows Settings that this utility didn't pick up. May need more work. If this happens to you, the diagnostics dump (see *Usage*) is the place to start.

#
### Customization locations

- **Low-battery color threshold:** `BatteryIcon.cs:140` (`LowBatteryPercent`). There's no config file, since this is currently the only tunable value, and it's compile-time only.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
