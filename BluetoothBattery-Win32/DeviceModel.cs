namespace BluetoothBattery;

internal enum BatterySource
{
    None,

    // GATT Battery Service 0x180F, characteristic 0x2A19.
    GattBatteryService,

    DeviceProperty,

    // DEVPKEY_Bluetooth_Battery read from the PnP device tree through SetupAPI.
    PnpDeviceProperty,

    ContainerProperty,
}

internal sealed class DeviceModel
{
    internal required string Id { get; init; }

    internal required string Name { get; set; }

    internal string? Address { get; set; }

    internal string? ContainerId { get; set; }

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
