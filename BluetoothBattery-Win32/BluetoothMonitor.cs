using System.Runtime.InteropServices;

namespace BluetoothBattery;

internal sealed partial class BluetoothMonitor : IDisposable
{
    internal BluetoothMonitor(UiDispatcher dispatcher, MessageWindow window)
    {
        _dispatcher = dispatcher;
        _window = window;
        _window.DeviceInterfaceChanged += OnDeviceInterfaceChanged;
        _window.BluetoothCustomEvent += OnBluetoothCustomEvent;
    }

    internal event Action? DevicesChanged;

    internal RadioState RadioState { get; private set; } = RadioState.Unknown;

    internal bool RadioIsOn => RadioState == RadioState.On;

    private readonly UiDispatcher _dispatcher;
    private readonly MessageWindow _window;
    private readonly Dictionary<string, DeviceModel> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private BluetoothRadio? _radio;
    private IntPtr _radioNotifyHandle = IntPtr.Zero;
    private IntPtr _rangeNotifyHandle = IntPtr.Zero;
    private bool _disposed;

    internal IReadOnlyList<DeviceView> Snapshot()
    {
        lock (_gate)
        {
            return _devices.Values
                .Where(d => d.IsConnected)
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(DeviceView.From)
                .ToList();
        }
    }

    private IReadOnlyList<DeviceModel> LiveDevices()
    {
        lock (_gate)
        {
            return _devices.Values.Where(d => d.IsConnected).ToList();
        }
    }

    internal async Task StartAsync()
    {
        using var _timing = Timing.Measure("BluetoothMonitor.StartAsync");

        await InitializeRadioAsync();
        RegisterNotifications();
        await FullSweepAsync();
    }

    private async Task InitializeRadioAsync()
    {
        try
        {
            _radio = await BluetoothRadio.OpenAsync();

            if (_radio is null)
            {
                Diagnostics.Write("No Bluetooth radio found on this machine.");
                RadioState = RadioState.Disabled;
                return;
            }

            RadioState = _radio.ReadState();
            Diagnostics.Write($"Bluetooth radio found ({_radio.ReadName() ?? "unnamed"}), state: {RadioState}");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Radio initialization failed: {ex.Message}");
            RadioState = RadioState.Unknown;
        }
    }

    private void RegisterNotifications()
    {
        _radioNotifyHandle = RegisterFor(BluetoothPortDeviceInterface);

        if (_radio is not null && _radio.Handle != IntPtr.Zero)
        {
            _rangeNotifyHandle = RegisterForRadioHandle(_radio.Handle);
        }
        else
        {
            Diagnostics.Write("No Win32 radio handle available yet - device connect/disconnect " +
                              "notifications cannot be registered this session, so far.");
        }
    }

    private async Task TryAcquireRadioHandleAsync()
    {
        if (_rangeNotifyHandle != IntPtr.Zero || _radio is null)
        {
            return;
        }

        if (_radio.Handle != IntPtr.Zero)
        {
            _rangeNotifyHandle = RegisterForRadioHandle(_radio.Handle);
            return;
        }

        var fresh = await BluetoothRadio.OpenAsync();
        if (fresh is not null && fresh.Handle != IntPtr.Zero)
        {
            Diagnostics.Write("Acquired a Win32 radio handle on retry - " +
                              "registering the handle-based notification now.");
            _radio.Dispose();
            _radio = fresh;
            _rangeNotifyHandle = RegisterForRadioHandle(_radio.Handle);
        }
        else
        {
            fresh?.Dispose();
        }
    }

    // GUID_BTHPORT_DEVICE_INTERFACE - the Bluetooth radio's own device interface.
    private static readonly Guid BluetoothPortDeviceInterface = new("0850302a-b344-4fda-9be9-90576b8d46f0");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE_FILTER
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    private IntPtr RegisterFor(Guid interfaceClass)
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE_FILTER
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE_FILTER>(),
            dbcc_devicetype = Win32.DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = interfaceClass,
        };

        var buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            var handle = Win32.RegisterDeviceNotificationW(
                _window.Handle, buffer, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);

            if (handle == IntPtr.Zero)
            {
                Diagnostics.Write($"RegisterDeviceNotification failed for {interfaceClass} " +
                                  $"(Win32 {Marshal.GetLastWin32Error()}).");
            }
            else
            {
                Diagnostics.Write($"RegisterDeviceNotification succeeded for {interfaceClass}.");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr RegisterForRadioHandle(IntPtr radioHandle)
    {
        var filter = new Win32.DEV_BROADCAST_HANDLE
        {
            dbch_size = Marshal.SizeOf<Win32.DEV_BROADCAST_HANDLE>(),
            dbch_devicetype = Win32.DBT_DEVTYP_HANDLE,
            dbch_handle = radioHandle,
        };

        var buffer = Marshal.AllocHGlobal(filter.dbch_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            var handle = Win32.RegisterDeviceNotificationW(
                _window.Handle, buffer, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);

            if (handle == IntPtr.Zero)
            {
                Diagnostics.Write($"RegisterDeviceNotification (handle-based) failed " +
                                  $"(Win32 {Marshal.GetLastWin32Error()}).");
            }
            else
            {
                Diagnostics.Write("RegisterDeviceNotification (handle-based, radio) succeeded.");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private async void OnDeviceInterfaceChanged(int eventType, Win32.DEV_BROADCAST_DEVICEINTERFACE header)
    {
        try
        {
            if (header.dbcc_classguid == BluetoothPortDeviceInterface)
            {
                await RecheckRadioStateAsync();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Device interface notification handler failed: {ex.Message}");
        }
    }

    private async void OnBluetoothCustomEvent(Guid eventGuid)
    {
        try
        {
            if (eventGuid == BluetoothRadio.RadioInRange || eventGuid == BluetoothRadio.RadioOutOfRange)
            {
                await CatchUpAsync();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Bluetooth custom-event handler failed: {ex.Message}");
        }
    }

    internal async Task CatchUpAsync()
    {
        lock (_gate)
        {
            if (_catchUpInFlight)
            {
                _catchUpPending = true;
                _catchUpCoalesced++;
                return;
            }

            _catchUpInFlight = true;
        }

        var passes = 0;
        var coalescedThisRun = 0;

        try
        {
            while (true)
            {
                passes++;
                await RecheckRadioStateAsync();

                if (RadioIsOn)
                {
                    await FullSweepAsync();
                }

                lock (_gate)
                {
                    if (!_catchUpPending)
                    {
                        break;
                    }

                    _catchUpPending = false;
                    coalescedThisRun += _catchUpCoalesced;
                    _catchUpCoalesced = 0;
                }
            }

            if (coalescedThisRun > 0)
            {
                Diagnostics.Write($"CatchUpAsync coalesced {coalescedThisRun} duplicate trigger(s) into {passes} pass(es).");
            }
        }
        finally
        {
            lock (_gate)
            {
                _catchUpInFlight = false;
            }
        }
    }

    private bool _catchUpInFlight;
    private bool _catchUpPending;
    private int _catchUpCoalesced;

    private async Task RecheckRadioStateAsync()
    {
        try
        {
            await TryAcquireRadioHandleAsync();

            var previous = RadioState;
            RadioState = _radio?.ReadState() ?? RadioState.Disabled;

            if (previous == RadioState)
            {
                return;
            }

            Diagnostics.Write($"Radio state changed: {previous} -> {RadioState}");

            if (RadioState == RadioState.On)
            {
                await FullSweepAsync();
            }
            else
            {
                lock (_gate)
                {
                    foreach (var device in _devices.Values)
                    {
                        device.IsConnected = false;
                    }
                }

                RaiseDevicesChanged();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Radio state recheck failed: {ex.Message}");
        }
    }

    private async Task FullSweepAsync()
    {
        using var _timing = Timing.Measure("FullSweepAsync");

        if (!RadioIsOn)
        {
            Diagnostics.Write("Sweep skipped - Bluetooth radio is not on.");
            return;
        }

        var found = await Task.Run(EnumeratePaired);

        List<DeviceModel> newlyConnected;
        lock (_gate)
        {
            var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            newlyConnected = new List<DeviceModel>();

            foreach (var (address, name, connected) in found)
            {
                seenAddresses.Add(address);

                if (!connected)
                {
                    _devices.Remove(address);
                    continue;
                }

                if (!_devices.TryGetValue(address, out var device))
                {
                    device = new DeviceModel { Id = address, Name = name, Address = address, IsConnected = true };
                    _devices[address] = device;
                    newlyConnected.Add(device);
                }
                else
                {
                    device.Name = name;
                    device.IsConnected = true;
                }
            }

            var stale = _devices.Keys.Where(address => !seenAddresses.Contains(address)).ToList();
            foreach (var address in stale)
            {
                _devices.Remove(address);
            }
        }

        Diagnostics.Write($"Sweep: {found.Count} device(s) reported, " +
                          $"{LiveDevices().Count} connected, {newlyConnected.Count} newly connected.");

        await RefreshBatteryAsync(LiveDevices());
        RaiseDevicesChanged();
    }

    private static List<(string Address, string Name, bool Connected)> EnumeratePaired()
    {
        var results = new List<(string, string, bool)>();
        results.AddRange(EnumerateClassic());
        results.AddRange(EnumerateBle());
        return results;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    private static List<(string, string, bool)> EnumerateClassic()
    {
        var results = new List<(string, string, bool)>();

        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnConnected = true,
            fReturnUnknown = false,
            fIssueInquiry = false,
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero,
        };

        var info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };

        var findHandle = BluetoothFindFirstDevice(ref search, ref info);
        if (findHandle == IntPtr.Zero)
        {
            return results;
        }

        try
        {
            do
            {
                if (info.fConnected)
                {
                    results.Add((AddressToString(info.Address), info.szName, true));
                }

                info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
            } while (BluetoothFindNextDevice(findHandle, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(findHandle);
        }

        return results;
    }

    private static string AddressToString(ulong address)
    {
        // BLUETOOTH_ADDRESS packs the 6-byte address into the low 48 bits.
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("x2")));
    }

    private static List<(string, string, bool)> EnumerateBle()
    {
        var results = new List<(string, string, bool)>();
        var interfaceGuid = BleDeviceInterfaceForEnumeration;

        var deviceInfoSet = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
        {
            return results;
        }

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                {
                    break;
                }

                SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0,
                    out var required, IntPtr.Zero);
                if (required == 0)
                {
                    continue;
                }

                var detailBuffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                    if (!SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, detailBuffer,
                            required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                    var address = path is null ? null : PnpBattery.ExtractAddress(path);
                    if (address is not null)
                    {
                        results.Add((address, address, true));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    private static readonly Guid BleDeviceInterfaceForEnumeration = new("781aee18-7733-4ce4-add0-091f4ddd3319");

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    internal async Task RefreshAsync()
    {
        if (!RadioIsOn)
        {
            Diagnostics.Write("Refresh skipped - Bluetooth radio is not on.");
            return;
        }

        await RefreshBatteryAsync(LiveDevices());
        RaiseDevicesChanged();
    }

    private static async Task RefreshBatteryAsync(IReadOnlyList<DeviceModel> devices)
    {
        using var _timing = Timing.Measure("RefreshBatteryAsync");

        var pnpBattery = await Task.Run(() =>
        {
            using var _sweep = Timing.Measure("PnpBattery.ReadBatteryByAddress");
            return PnpBattery.ReadBatteryByAddress();
        });
        Diagnostics.Write($"PnP battery source reported {pnpBattery.Count} device(s) with a level.");

        var pnpContainerIds = await Task.Run(() =>
        {
            using var _sweep = Timing.Measure("PnpBattery.ReadContainerIdsByAddress");
            return PnpBattery.ReadContainerIdsByAddress();
        });

        var containerBattery = new Lazy<Task<Dictionary<string, int>>>(async () =>
        {
            using var _sweep = Timing.Measure("ContainerBattery.ReadByContainerAsync");
            var read = await ContainerBattery.ReadByContainerAsync();
            Diagnostics.Write($"Container battery source reported {read.Count} device(s) with a level.");
            return read;
        });

        foreach (var device in devices)
        {
            if (device.Address is not null &&
                pnpContainerIds.TryGetValue(PnpBattery.NormalizeAddress(device.Address), out var containerId))
            {
                device.ContainerId = containerId;
            }

            await UpdateBatteryAsync(device, pnpBattery, containerBattery);
        }

        foreach (var device in devices)
        {
            if (device.BatteryPercent is null)
            {
                Diagnostics.Write($"{device.Name}: no battery after all sources.");
            }
        }
    }

    private static async Task UpdateBatteryAsync(
        DeviceModel device,
        IReadOnlyDictionary<string, int> pnpBattery,
        Lazy<Task<Dictionary<string, int>>> containerBattery)
    {
        using var _timing = Timing.Measure($"UpdateBattery({device.Name})");

        var previous = device.RawBatteryPercent;
        var previousSource = device.Source;
        device.RawBatteryPercent = null;
        device.Source = BatterySource.None;

        if (device.Address is not null)
        {
            var key = PnpBattery.NormalizeAddress(device.Address);
            if (pnpBattery.TryGetValue(key, out var level))
            {
                device.BatteryPercent = level;
                device.Source = BatterySource.PnpDeviceProperty;
                device.LastUpdated = DateTime.UtcNow;
                Diagnostics.Write($"{device.Name}: {level}% from the PnP device tree.");
            }
        }

        if (device.RawBatteryPercent is null && device.ContainerId is not null)
        {
            var containers = await containerBattery.Value;
            if (containers.TryGetValue(device.ContainerId, out var containerLevel))
            {
                device.BatteryPercent = containerLevel;
                device.Source = BatterySource.ContainerProperty;
                device.LastUpdated = DateTime.UtcNow;
                Diagnostics.Write($"{device.Name}: {containerLevel}% from the device container.");
            }
        }

        if (device.RawBatteryPercent is null && device.Address is not null)
        {
            var gattLevel = await Task.Run(() => GattBattery.TryReadBattery(PnpBattery.NormalizeAddress(device.Address)));
            if (gattLevel.HasValue)
            {
                device.BatteryPercent = gattLevel.Value;
                device.Source = BatterySource.GattBatteryService;
                device.LastUpdated = DateTime.UtcNow;
            }
        }

        if (device.RawBatteryPercent is null && previous is not null)
        {
            device.RawBatteryPercent = previous;
            device.Source = previousSource;
        }
    }

    internal static Task<IReadOnlyList<RawDeviceInfo>> EnumerateRawAsync()
    {
        var results = PnpBattery.Enumerate()
            .Select(d => (RawDeviceInfo)new(
                d.InstanceId,
                d.Name ?? "(unnamed device)",
                d.InstanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ? "LE" : "Classic",
                BuildRawProperties(d)))
            .ToList();

        return Task.FromResult<IReadOnlyList<RawDeviceInfo>>(results);
    }

    private static IReadOnlyDictionary<string, object?> BuildRawProperties(PnpBattery.PnpDevice device) =>
        new Dictionary<string, object?>
        {
            ["Address"] = device.Address,
            ["Battery"] = device.Battery,
        };

    private void RaiseDevicesChanged() => _dispatcher.Post(() => DevicesChanged?.Invoke());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _window.DeviceInterfaceChanged -= OnDeviceInterfaceChanged;
        _window.BluetoothCustomEvent -= OnBluetoothCustomEvent;

        if (_radioNotifyHandle != IntPtr.Zero)
        {
            Win32.UnregisterDeviceNotification(_radioNotifyHandle);
        }

        if (_rangeNotifyHandle != IntPtr.Zero)
        {
            Win32.UnregisterDeviceNotification(_rangeNotifyHandle);
        }

        _radio?.Dispose();
    }
}

internal sealed record RawDeviceInfo(string Id, string Name, string Kind, IReadOnlyDictionary<string, object?> Properties);
