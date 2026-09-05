using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace BluetoothBattery;

internal sealed partial class BluetoothMonitor : IDisposable
{
    // Probed individually at startup: Windows rejects an entire property
    // request if any single key in it is unrecognized.
    private static readonly string[] BatteryPropertyCandidates =
    {
        "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", // DEVPKEY_Bluetooth_Battery
        "System.Devices.BatteryLife",
        "System.Devices.BatteryPercentage",
    };

    private static readonly string[] ConnectionPropertyCandidates =
    {
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.ContainerId",
    };

    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";

    private static Dictionary<string, BluetoothProfiles.DeviceProfiles>? _profileCache;
    private static DateTime _profileCacheTime = DateTime.MinValue;
    private static readonly object _profileGate = new();

    private static BluetoothProfiles.DeviceProfiles? ProfilesFor(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        lock (_profileGate)
        {
            if (_profileCache is null || DateTime.UtcNow - _profileCacheTime > TimeSpan.FromSeconds(5))
            {
                try
                {
                    _profileCache = BluetoothProfiles.ScanByAddress();
                    _profileCacheTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Diagnostics.Write($"Profile scan failed: {ex.Message}");
                    return null;
                }
            }

            return _profileCache.TryGetValue(PnpBattery.NormalizeAddress(address), out var found)
                ? found
                : null;
        }
    }
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string ContainerProperty = "System.Devices.Aep.ContainerId";

    private static string[] _supportedProperties = Array.Empty<string>();
    private static string[] _supportedBatteryProperties = Array.Empty<string>();

    private readonly Dictionary<string, DeviceModel> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly UiDispatcher _dispatcher;

    private int _enumerating;

    private DeviceWatcher? _classicWatcher;
    private DeviceWatcher? _bleWatcher;
    private Radio? _radio;
    private bool _disposed;

    internal BluetoothMonitor(UiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    internal event Action? DevicesChanged;


    internal RadioState RadioState { get; private set; } = RadioState.Unknown;

    internal bool RadioIsOn => RadioState == RadioState.On;

    internal IReadOnlyList<DeviceView> Snapshot()
    {
        lock (_gate)
        {
            return SelectVisible()
                .Select(DeviceView.From)
                .ToList();
        }
    }

    private IReadOnlyList<DeviceModel> LiveDevices()
    {
        lock (_gate)
        {
            return SelectVisible().ToList();
        }
    }

    private List<DeviceModel> SelectVisible()
    {
        var candidates = _devices.Values;

        var merged = new List<DeviceModel>();
        var byAddress = new Dictionary<string, DeviceModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in candidates)
        {
            if (string.IsNullOrEmpty(device.Address))
            {
                merged.Add(device);
                continue;
            }

            if (!byAddress.TryGetValue(device.Address, out var existing))
            {
                byAddress[device.Address] = device;
                merged.Add(device);
                continue;
            }

            if (Prefer(device, existing))
            {
                byAddress[device.Address] = device;
                merged[merged.IndexOf(existing)] = device;
            }
        }

        return merged
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool Prefer(DeviceModel candidate, DeviceModel current)
    {
        var candidateHasReading = candidate.BatteryPercent.HasValue;
        var currentHasReading = current.BatteryPercent.HasValue;

        if (candidateHasReading != currentHasReading)
        {
            return candidateHasReading;
        }

        return candidate.IsConnected && !current.IsConnected;
    }

    internal async Task StartAsync()
    {
        using var _timing = Timing.Measure("BluetoothMonitor.StartAsync");

        await InitializeRadioAsync();
        await ProbeSupportedPropertiesAsync();
        StartWatchers();
    }

    private static async Task ProbeSupportedPropertiesAsync()
    {
        using var _timing = Timing.Measure("ProbeSupportedPropertiesAsync");

        var cached = Program.Cache;
        if (cached.CachedProperties.Length > 0)
        {
            _supportedProperties = cached.CachedProperties;
            _supportedBatteryProperties = cached.CachedBatteryProperties;

            Diagnostics.Write(
                $"Property keys read from cache.ini - probe skipped. " +
                $"Battery property source(s): {string.Join(", ", _supportedBatteryProperties)}");
            return;
        }

        string selector;
        try
        {
            selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not build a device selector to probe properties: {ex.Message}");
            return;
        }

        var accepted = new List<string>();
        var acceptedBattery = new List<string>();

        // Probed one key at a time; a single unrecognized key rejects a batched request.
        foreach (var key in BatteryPropertyCandidates.Concat(ConnectionPropertyCandidates))
        {
            try
            {
                await DeviceInformation.FindAllAsync(
                    selector, new[] { key }, DeviceInformationKind.AssociationEndpoint);

                accepted.Add(key);
                if (BatteryPropertyCandidates.Contains(key))
                {
                    acceptedBattery.Add(key);
                }

                Diagnostics.Write($"Property key accepted: {key}");
            }
            catch (Exception ex)
            {
                Diagnostics.Write($"Property key rejected: {key} - {ex.Message}");
            }
        }

        _supportedProperties = accepted.ToArray();
        _supportedBatteryProperties = acceptedBattery.ToArray();

        Diagnostics.Write(acceptedBattery.Count > 0
            ? $"Battery property source(s): {string.Join(", ", acceptedBattery)}"
            : "No battery property key accepted - falling back to GATT reads only.");

        if (accepted.Count > 0)
        {
            cached.SaveProbeResult(_supportedProperties, _supportedBatteryProperties);
        }
    }

    internal async Task RefreshAsync()
    {
        using var _timing = Timing.Measure("RefreshAsync");

        if (!RadioIsOn)
        {
            Diagnostics.Write("Refresh skipped - Bluetooth radio is not on.");
            return;
        }

        var pnpBattery = await Task.Run(() =>
        {
            using var _sweep = Timing.Measure("PnpBattery.ReadBatteryByAddress");
            return PnpBattery.ReadBatteryByAddress();
        });
        Diagnostics.Write($"PnP battery source reported {pnpBattery.Count} device(s) with a level.");

        var containerBattery = LazyContainerRead();

        foreach (var device in LiveDevices())
        {
            await UpdateBatteryAsync(device, pnpBattery, containerBattery);
        }

        foreach (var device in LiveDevices())
        {
            if (device.BatteryPercent is null)
            {
                Diagnostics.Write(
                    $"{device.Name}: no battery after all sources - " +
                    BluetoothProfiles.ExplainMissingBattery(ProfilesFor(device.Address), device.IsConnected));
            }
        }

        RaiseDevicesChanged();
    }

    // -----------------------------------------------------------------------
    // Radio
    // -----------------------------------------------------------------------

    private async Task InitializeRadioAsync()
    {
        using var _timing = Timing.Measure("InitializeRadioAsync");

        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Diagnostics.Write($"Radio control access is {access}. Not required - this utility " +
                                  "only reads radio state, never changes it.");
            }

            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

            if (_radio is null)
            {
                Diagnostics.Write("No Bluetooth radio found on this machine.");
                RadioState = RadioState.Disabled;
                return;
            }

            RadioState = _radio.State;
            _radio.StateChanged += OnRadioStateChanged;
            Diagnostics.Write($"Bluetooth radio found, state: {RadioState}");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Radio initialization failed: {ex.Message}");
            RadioState = RadioState.Unknown;
        }
    }

    private async void OnRadioStateChanged(Radio sender, object args)
    {
        try
        {
            var previous = RadioState;
            RadioState = sender.State;

            // Windows raises StateChanged for transitions that are not changes.
            if (previous == RadioState)
            {
                return;
            }

            Diagnostics.Write($"Radio state changed: {previous} -> {RadioState}");

            if (RadioState == RadioState.On)
            {
                await RefreshAsync();
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
            }

            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Radio state handler failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Device watchers
    // -----------------------------------------------------------------------

    private void StartWatchers()
    {
        _classicWatcher = CreateWatcher(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true), "classic");

        _bleWatcher = CreateWatcher(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), "LE");
    }

    private DeviceWatcher? CreateWatcher(string selector, string label)
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(
                selector, _supportedProperties, DeviceInformationKind.AssociationEndpoint);

            watcher.Added += OnDeviceAdded;
            watcher.Updated += OnDeviceUpdated;
            watcher.Removed += OnDeviceRemoved;
            watcher.EnumerationCompleted += OnEnumerationCompleted;

            Interlocked.Increment(ref _enumerating);

            try
            {
                watcher.Start();
            }
            catch
            {
                Interlocked.Decrement(ref _enumerating);
                throw;
            }

            Diagnostics.Write($"Started {label} Bluetooth device watcher.");
            return watcher;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not start {label} device watcher: {ex.Message}");
            return null;
        }
    }

    private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        try
        {
            var connected = IsConnectedIn(info.Properties);

            if (!connected)
            {
                lock (_gate)
                {
                    _devices.Remove(info.Id);
                }

                RaiseDevicesChanged();
                return;
            }

            DeviceModel device;
            lock (_gate)
            {
                if (!_devices.TryGetValue(info.Id, out var existing))
                {
                    existing = new DeviceModel { Id = info.Id, Name = DisplayName(info) };
                    _devices[info.Id] = existing;
                }
                else
                {
                    existing.Name = DisplayName(info);
                }

                device = existing;
            }

            ApplyProperties(device, info.Properties);

            var profiles = ProfilesFor(device.Address);

            Diagnostics.Write(
                $"Device added: {device.Name} ({device.BatteryText}), " +
                $"{(device.IsConnected ? "CONNECTED" : "paired but not connected")}" +
                (profiles is null ? string.Empty : $", profiles: {profiles.Describe()}"));

            // During the initial enumeration burst, reads are coalesced into one
            // sweep in OnEnumerationCompleted rather than done per device here.
            if (Volatile.Read(ref _enumerating) > 0)
            {
                RaiseDevicesChanged();
                return;
            }

            await UpdateBatteryAsync(
                device,
                PnpBattery.ReadBatteryByAddress(),
                LazyContainerRead());
            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Device added handler failed: {ex.Message}");
        }
    }

    private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            DeviceModel? device;
            lock (_gate)
            {
                _devices.TryGetValue(update.Id, out device);
            }

            if (device is null)
            {
                if (!IsConnectedIn(update.Properties))
                {
                    return;
                }

                Diagnostics.Write($"Untracked device connected ({update.Id}) - adding it.");
                await AddConnectedAsync(update.Id);
                return;
            }

            var wasConnected = device.IsConnected;
            ApplyProperties(device, update.Properties);
            RaiseDevicesChanged();

            if (!wasConnected && device.IsConnected)
            {
                Diagnostics.Write($"{device.Name} connected - reading battery.");
                _ = RefreshAsync();
            }
            else if (wasConnected && !device.IsConnected)
            {
                lock (_gate)
                {
                    _devices.Remove(device.Id);
                }

                Diagnostics.Write($"{device.Name} disconnected - dropped from the list.");
                RaiseDevicesChanged();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Device updated handler failed: {ex.Message}");
        }
    }

    private async void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        try
        {
            // Two watchers (classic and LE) each fire this; the counter waits
            // for both before running the one combined sweep below.
            if (Interlocked.Decrement(ref _enumerating) > 0)
            {
                return;
            }

            var devices = LiveDevices();

            Diagnostics.Write(
                $"Initial enumeration complete: {devices.Count} connected device(s). " +
                "Reading battery once for all of them.");

            var pnp = PnpBattery.ReadBatteryByAddress();
            var containers = LazyContainerRead();

            foreach (var device in devices)
            {
                await UpdateBatteryAsync(device, pnp, containers);
            }

            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Enumeration completed handler failed: {ex.Message}");
        }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        try
        {
            lock (_gate)
            {
                if (_devices.Remove(update.Id, out var removed))
                {
                    Diagnostics.Write($"Device removed: {removed.Name}");
                }
            }

            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Device removed handler failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Battery reading
    // -----------------------------------------------------------------------

    // Deferred until a device actually reaches this stage of the priority
    // chain; shared per refresh so the container sweep runs at most once.
    private static Lazy<Task<IReadOnlyDictionary<string, int>>> LazyContainerRead() =>
        new(async () =>
        {
            using var _sweep = Timing.Measure("ContainerBattery.ReadByContainerAsync");
            var read = await ContainerBattery.ReadByContainerAsync();
            Diagnostics.Write(
                $"Container battery source reported {read.Count} device(s) with a level.");
            return read;
        });

    private async Task UpdateBatteryAsync(
        DeviceModel device,
        IReadOnlyDictionary<string, int> pnpBattery,
        Lazy<Task<IReadOnlyDictionary<string, int>>> containerBattery)
    {
        using var _timing = Timing.Measure($"UpdateBattery({device.Name})");

        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(
                device.Id, _supportedProperties, DeviceInformationKind.AssociationEndpoint);

            if (info is not null)
            {
                device.Name = DisplayName(info);
                ApplyProperties(device, info.Properties);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Property read failed for {device.Name}: {ex.Message}");
        }

        // Priority chain: PnP, then container, then GATT, with the WinRT value
        // (set by ApplyProperties above) restored as fallback if nothing below finds one.
        var previous = device.RawBatteryPercent;
        var previousSource = device.Source;
        device.RawBatteryPercent = null;
        device.Source = BatterySource.None;

        if (device.RawBatteryPercent is null && !string.IsNullOrEmpty(device.Address))
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

        if (device.RawBatteryPercent is null && device.IsConnected &&
            !string.IsNullOrEmpty(device.ContainerId))
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

        if (device.RawBatteryPercent is null && device.IsConnected)
        {
            var gattLevel = await TryReadGattBatteryAsync(device.Id);
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

    private static async Task<int?> TryReadGattBatteryAsync(string deviceId)
    {
        using var _timing = Timing.Measure("GATT read");

        // GATT applies to Bluetooth LE only; a classic "Bluetooth#" id passed to
        // BluetoothLEDevice.FromIdAsync throws.
        if (!deviceId.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;

        try
        {
            device = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (device is null)
            {
                return null;
            }

            var services = await device.GetGattServicesForUuidAsync(
                GattServiceUuids.Battery, BluetoothCacheMode.Uncached);

            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                return null;
            }

            service = services.Services[0];

            var characteristics = await service.GetCharacteristicsForUuidAsync(
                GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);

            if (characteristics.Status != GattCommunicationStatus.Success ||
                characteristics.Characteristics.Count == 0)
            {
                return null;
            }

            var read = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value.Length == 0)
            {
                return null;
            }

            using var reader = DataReader.FromBuffer(read.Value);
            int level = reader.ReadByte();
            return level is >= 0 and <= 100 ? level : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"GATT battery read unavailable for {deviceId}: {ex.Message}");
            return null;
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
        }
    }

    private async Task AddConnectedAsync(string id)
    {
        using var _timing = Timing.Measure("AddConnectedAsync");

        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(
                id, _supportedProperties, DeviceInformationKind.AssociationEndpoint);

            if (info is null || !IsConnectedIn(info.Properties))
            {
                return;
            }

            DeviceModel device;
            lock (_gate)
            {
                if (!_devices.TryGetValue(id, out var existing))
                {
                    existing = new DeviceModel { Id = id, Name = DisplayName(info) };
                    _devices[id] = existing;
                }

                device = existing;
            }

            ApplyProperties(device, info.Properties);
            await UpdateBatteryAsync(device, PnpBattery.ReadBatteryByAddress(), LazyContainerRead());
            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Adding a newly connected device failed: {ex.Message}");
        }
    }

    private static bool IsConnectedIn(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(ConnectedProperty, out var value) && value is true;

    private static void ApplyProperties(DeviceModel device, IReadOnlyDictionary<string, object> properties)
    {
        // Candidates are ordered most-likely-first; the first accepted key wins.
        foreach (var key in _supportedBatteryProperties)
        {
            if (properties.TryGetValue(key, out var raw) && raw is not null &&
                TryToPercent(raw, out var percent))
            {
                device.BatteryPercent = percent;
                device.Source = BatterySource.DeviceProperty;
                device.LastUpdated = DateTime.UtcNow;
                break;
            }
        }

        if (properties.TryGetValue(ConnectedProperty, out var connected) && connected is bool isConnected)
        {
            device.IsConnected = isConnected;
        }

        if (properties.TryGetValue(AddressProperty, out var address) && address is string addressText &&
            !string.IsNullOrWhiteSpace(addressText))
        {
            device.Address = addressText;
        }

        if (properties.TryGetValue(ContainerProperty, out var container) && container is not null)
        {
            var text = container is Guid guid ? guid.ToString("B") : container.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                device.ContainerId = text;
            }
        }
    }

    private static bool TryToPercent(object raw, out int percent)
    {
        try
        {
            percent = Convert.ToInt32(raw);
            return percent is >= 0 and <= 100;
        }
        catch
        {
            percent = 0;
            return false;
        }
    }


    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    internal static async Task<IReadOnlyList<DeviceInformation>> EnumerateRawAsync()
    {
        var results = new List<DeviceInformation>();

        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                var found = await DeviceInformation.FindAllAsync(
                    selector, _supportedProperties, DeviceInformationKind.AssociationEndpoint);
                results.AddRange(found);
            }
            catch (Exception ex)
            {
                Diagnostics.Write($"Raw enumeration with properties failed: {ex.Message}");

                try
                {
                    var fallback = await DeviceInformation.FindAllAsync(
                        selector, Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint);
                    results.AddRange(fallback);
                    Diagnostics.Write($"Retried without properties: {fallback.Count} device(s).");
                }
                catch (Exception retryEx)
                {
                    Diagnostics.Write($"Raw enumeration failed entirely: {retryEx.Message}");
                }
            }
        }

        return results;
    }

    private static string DisplayName(DeviceInformation info) =>
        string.IsNullOrWhiteSpace(info.Name) ? "(unnamed device)" : info.Name;

    private void RaiseDevicesChanged() => Post(() => DevicesChanged?.Invoke());

    private void Post(Action action) => _dispatcher.Post(action);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_radio is not null)
        {
            _radio.StateChanged -= OnRadioStateChanged;
        }

        foreach (var watcher in new[] { _classicWatcher, _bleWatcher })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.Added -= OnDeviceAdded;
                watcher.Updated -= OnDeviceUpdated;
                watcher.Removed -= OnDeviceRemoved;
                watcher.EnumerationCompleted -= OnEnumerationCompleted;

                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Write($"Stopping a device watcher failed: {ex.Message}");
            }
        }
    }
}
