namespace BluetoothBattery;

internal enum BatterySource
{
    None,
    GattBatteryService,
    DeviceProperty,
    PnpDeviceProperty,
    ContainerProperty,
}

internal sealed class DeviceModel
{
    internal required string Id { get; init; }

    internal required string Name { get; set; }

    internal string? Address { get; set; }

    internal string? ContainerId { get; set; }

    // Null while disconnected, regardless of the last raw reading - Windows
    // otherwise keeps handing out a stale value indefinitely.
    internal int? BatteryPercent
    {
        get => IsConnected ? RawBatteryPercent : null;
        set => RawBatteryPercent = value;
    }

    internal int? RawBatteryPercent { get; set; }

    internal BatterySource Source { get; set; } = BatterySource.None;

    internal DateTime? LastUpdated { get; set; }

    internal bool IsConnected { get; set; }

    internal string BatteryText => BatteryPercent.HasValue
        ? $"{BatteryPercent.Value}%"
        : "N/A";

    internal string LastUpdatedText => LastUpdated.HasValue
        ? LastUpdated.Value.ToLocalTime().ToString("HH:mm:ss")
        : "never";
}

// A frozen copy of one device, safe to hand to another thread - DeviceModel's
// fields are written by WinRT callbacks on thread-pool threads, and
// BatteryPercent is computed from two of them, so reading it twice (HasValue,
// then Value) can race and throw. Take the copy while holding the monitor's
// lock.
internal sealed record DeviceView(
    string Id,
    string Name,
    string? Address,
    string? ContainerId,
    int? BatteryPercent,
    int? RawBatteryPercent,
    BatterySource Source,
    DateTime? LastUpdated,
    bool IsConnected)
{
    internal static DeviceView From(DeviceModel device) => new(
        device.Id,
        device.Name,
        device.Address,
        device.ContainerId,
        device.BatteryPercent,
        device.RawBatteryPercent,
        device.Source,
        device.LastUpdated,
        device.IsConnected);

    internal string BatteryText => BatteryPercent.HasValue
        ? $"{BatteryPercent.Value}%"
        : (RawBatteryPercent.HasValue && !IsConnected
            ? $"N/A (last seen {RawBatteryPercent.Value}%)"
            : "N/A");

    internal string LastUpdatedText => LastUpdated.HasValue
        ? LastUpdated.Value.ToLocalTime().ToString("HH:mm:ss")
        : "never";
}
