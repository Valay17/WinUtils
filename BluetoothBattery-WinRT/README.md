<div id="user-content-toc" align="center">
  <ul>
    <img src="../svg-icons/BluetoothBattery.svg" alt="Bluetooth Battery (WinRT) icon" align="center" width="120" height="120">
    <summary><h1><p>Bluetooth Battery (WinRT)</p></h1></summary>
  </ul>
</div>

#
### Summary

The original WinRT-based implementation of the Bluetooth Battery tray utility, kept for comparison. It still compiles and runs, but is no longer the actively maintained build; a separate Win32/SetupAPI-based rewrite (in this same repository) is what actually gets used and fixed going forward.

#
### Features

- **Matches what Windows shows:** Reads battery through the same priority chain Windows Settings itself resolves through (a SetupAPI-backed property first, a container-id fallback, then a WinRT `AssociationEndpoint` property or a raw GATT read).
- **Profile-aware:** Correctly distinguishes which Bluetooth profiles actually carry a battery value (Hands-Free/Headset) from ones that never do (A2DP/AVRCP, music-only), rather than reporting "unavailable" without explanation.
- **On-demand refresh, no background polling:** `DeviceWatcher` delivers per-device Added/Updated/Removed events directly; nothing is fetched on a timer.
- **GATT fallback:** A raw GATT battery-service read exists for devices that don't expose battery through the classic path, used only when the classic sources come back empty.
- **On-demand diagnostics:** A built-in report dumps exactly what this utility sees for every known device (profiles, connection state, every source checked and its result), useful for debugging a specific device's behavior without needing to read the source.
- **Probe result caching:** the first launch probes which WinRT device properties this build's actual hardware/OS combination exposes for battery data, a WinRT-specific cost the Win32 build doesn't have (it never needs to probe WinRT properties in the first place). The result is cached in `cache.ini`, so every later launch skips the probe entirely.

#
### Why this needed .NET 9, specifically

Every other utility in this repository that uses native AOT targets .NET 8. This one needed .NET 9's updated CsWinRT AOT source generator to compile at all. .NET 8's generator could not produce a working AOT build for a WinRT call that hands a collection of strings across the ABI boundary (`Radio.GetRadiosAsync()`), an AOT/CCW gap that .NET 9's generator fixed. It is not a general "9 is better than 8" claim; the other five utilities have no such call and stay on the lighter .NET 8 toolchain.

#
### Why this isn't the active build any more

Memory captures showed roughly 4 MB added to the process the instant WinRT initializes, on top of what the tray/config/icon/popup layer needed on its own, plus roughly 1 MB more from actual use, mostly WinRT's own marshalling and projection metadata, not the DLLs it loads. Separate ETW tracing showed the WinRT Bluetooth DLL set itself costing a 44-169ms load window and several megabytes of commit activity right after process start. Classic-Bluetooth battery reads, which is what this utility actually needs, turned out to need none of that: the WinRT `AssociationEndpoint` battery property came back empty on every device tested, while the same devices' battery read correctly through the plain SetupAPI property both builds ultimately reach underneath. The replacement rewrite drops WinRT entirely in favor of direct SetupAPI enumeration, `RegisterDeviceNotification` for change events, and raw COM vtable calls for radio state, all reachable from Win32 alone.

#
### Footprint

Binary size: 4.9 MB. It includes the statically-bundled native runtime AOT produces, which is what lets the exe run on a machine with nothing else installed. A framework-dependent build (relying on a separately installed .NET runtime instead of bundling one) would be smaller, but would then need .NET pre-installed on whatever machine it runs on, which defeats the point of a portable, install-free utility.

This is the data that decided Win32 over WinRT: a vmmap capture from the round this build was directly compared against the Win32 rewrite. Roughly 3.9 MB working set at launch, before WinRT is touched at all; roughly +4.3 MB the instant WinRT initializes; roughly +1.0 MB more from actual use. That lands around 9 MB total, against the Win32 build's idle working set of roughly 6.1-6.7 MB. Separately, ETW tracing showed the WinRT Bluetooth DLL set costing a 44-169ms load window and several megabytes of commit activity right after process start, a cost the Win32 build does not pay at all. GPU usage doesn't apply here either: this utility draws nothing beyond a tray icon and the device popup, both handed to DWM (Desktop Window Manager, the OS component that draws every window to the screen) for compositing like any other window.

#
### Built with

C# / .NET 9, `net9.0-windows`, published as native AOT, WinRT projections (`DeviceWatcher`, `Radio.GetRadiosAsync`, `BluetoothLEDevice`/GATT, `Windows.Storage.Streams`, `Windows.Devices.Bluetooth`, `Windows.Devices.Enumeration`).

```
dotnet publish -c Release
```

#
### Usage

Usage is identical to the Win32 build: run `BluetoothBattery.exe`, click the tray icon for the per-device popup. There's no config file: nothing about this utility is currently user-configurable.

`cache.ini` is a separate file, created beside the executable the first time a probe result is actually cached (not on startup). It's a cache, not a setting: which WinRT device properties this build found battery data on, so it doesn't have to re-probe every launch. Its content:

```ini
; Bluetooth Battery - probe result cache, not a setting.
; Leave it alone.
; If every device reads N/A, delete this file; it rebuilds itself.

[probe]
properties=...
batteryProperties=...
```

#
### Mechanisms

Devices are discovered via `DeviceInformation.FindAllAsync` and tracked live with `DeviceWatcher`, which delivers per-device Added/Updated/Removed events directly rather than the Win32 build's coarser full-resweep-on-any-change approach. Radio on/off state comes from `Radio.GetRadiosAsync`. Classic battery values ultimately still come from the same SetupAPI property the Win32 build reads directly. The WinRT layer here sits on top of that, not underneath it, which is exactly why it turned out to be removable without losing functionality.

#
### Architecture

```
DeviceWatcher (Added / Updated / Removed events)
            |
            v
  Per device: WinRT battery property, then SetupAPI
  fallback (same property the Win32 build reads directly)
            |
            v
        Tray icon + popup updated
```

#
### Known limitations

- **Classic Bluetooth battery only works through the Hands-Free/Headset profile**, same platform boundary as the Win32 build. A2DP and AVRCP carry no battery capability at all.
- **This build is parked:** It's kept working and kept in the repository for the Win32-vs-WinRT comparison, but it doesn't get new fixes beyond what it needed to stay a fair comparison point. The Win32 build is where active development happens.

#
### Customization locations

- **Low-battery color threshold:** `BatteryIcon.cs:141` (`LowBatteryPercent`). There's no config file, since this is currently the only tunable value, and it's compile-time only. See *Usage* for `cache.ini`, which is a cache, not a setting.

#
### License

You are free to use and modify this software for personal or internal purposes. However, redistribution or public distribution of this software or any modified versions is not permitted without explicit permission.
