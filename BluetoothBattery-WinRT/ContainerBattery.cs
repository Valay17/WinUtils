using Windows.Devices.Enumeration.Pnp;

namespace BluetoothBattery;

// Reads battery from the device container - the object Windows Settings
// groups a device under. Containers are not device nodes, so the PnP/SetupAPI
// sweep in PnpBattery.cs cannot see them; this covers that gap separately.
internal static class ContainerBattery
{
    // Probed individually, same as elsewhere: Windows rejects an entire
    // property request if any one key is unrecognized.
    private static readonly string[] BatteryCandidates =
    {
        "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", // DEVPKEY_Bluetooth_Battery
        "System.Devices.BatteryLife",
        "System.Devices.BatteryPlusCharging",
    };

    private static readonly string[] DescriptiveProperties =
    {
        "System.ItemNameDisplay",
        "System.Devices.Connected",
    };

    internal static async Task<Dictionary<string, int>> ReadByContainerAsync()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var container in await EnumerateAsync())
        {
            if (container.Battery is { } level && !result.ContainsKey(container.Id))
            {
                result[container.Id] = level;
            }
        }

        return result;
    }

    internal sealed record ContainerInfo(string Id, string? Name, bool? Connected, int? Battery, string? Source);

    internal static async Task<IReadOnlyList<ContainerInfo>> EnumerateAsync()
    {
        var results = new List<ContainerInfo>();

        var accepted = new List<string>();
        foreach (var key in BatteryCandidates.Concat(DescriptiveProperties))
        {
            try
            {
                await PnpObject.FindAllAsync(PnpObjectType.DeviceContainer, new[] { key });
                accepted.Add(key);
            }
            catch (Exception ex)
            {
                Diagnostics.Write($"Container property rejected: {key} - {ex.Message}");
            }
        }

        if (accepted.Count == 0)
        {
            Diagnostics.Write("No container property keys were accepted - containers unusable as a source.");
            return results;
        }

        try
        {
            var containers = await PnpObject.FindAllAsync(PnpObjectType.DeviceContainer, accepted);

            foreach (var container in containers)
            {
                int? battery = null;
                string? source = null;

                foreach (var key in BatteryCandidates)
                {
                    if (container.Properties.TryGetValue(key, out var raw) && raw is not null &&
                        TryToPercent(raw, out var percent))
                    {
                        battery = percent;
                        source = key;
                        break;
                    }
                }

                container.Properties.TryGetValue("System.ItemNameDisplay", out var name);
                container.Properties.TryGetValue("System.Devices.Connected", out var connected);

                results.Add(new ContainerInfo(
                    container.Id,
                    name as string,
                    connected as bool?,
                    battery,
                    source));
            }

            Diagnostics.Write($"Enumerated {results.Count} device container(s), " +
                              $"{results.Count(c => c.Battery.HasValue)} with a battery value.");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Container enumeration failed: {ex.Message}");
        }

        return results;
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
}
