namespace BluetoothBattery;

internal static class BluetoothProfiles
{
    // Short-form service class UUIDs, inside the base 0000xxxx-0000-1000-8000-00805F9B34FB.
    private static readonly Dictionary<string, string> KnownServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1105"] = "OBEX Object Push",
        ["1108"] = "Headset (HSP)",
        ["110A"] = "Audio Source (A2DP)",
        ["110B"] = "Audio Sink (A2DP)",
        ["110C"] = "AVRCP Target",
        ["110E"] = "AVRCP Controller",
        ["1112"] = "Headset Audio Gateway",
        ["1115"] = "PAN",
        ["1116"] = "PAN NAP",
        ["111E"] = "Hands-Free (HFP)",
        ["111F"] = "Hands-Free Audio Gateway",
        ["112D"] = "SIM Access",
        ["112F"] = "Phonebook Access",
        ["1132"] = "Message Access",
        ["1200"] = "PnP Information",
        ["1800"] = "Generic Access (LE)",
        ["1801"] = "Generic Attribute (LE)",
        ["180F"] = "Battery Service (LE)",
    };

    // The only two classic-Bluetooth profiles that can deliver a battery level.
    private static readonly string[] BatteryCapableServices = { "111E", "1108" };

    // Battery Service over LE.
    private const string BatteryServiceLe = "180F";

    internal sealed record DeviceProfiles(string Address, IReadOnlyList<string> ServiceIds)
    {
        internal bool HasHandsFree =>
            ServiceIds.Any(id => BatteryCapableServices.Contains(id, StringComparer.OrdinalIgnoreCase));

        internal bool HasLeBatteryService =>
            ServiceIds.Contains(BatteryServiceLe, StringComparer.OrdinalIgnoreCase);

        internal bool CanReportBattery => HasHandsFree || HasLeBatteryService;

        internal string Describe() => ServiceIds.Count == 0
            ? "(no service nodes)"
            : string.Join(", ", ServiceIds.Select(Name));
    }

    private static string Name(string serviceId) =>
        KnownServices.TryGetValue(serviceId, out var name) ? name : $"unknown ({serviceId})";

    internal static Dictionary<string, DeviceProfiles> ScanByAddress()
    {
        var services = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in PnpBattery.Enumerate())
        {
            if (device.Address is null)
            {
                continue;
            }

            var serviceId = ExtractServiceId(device.InstanceId);
            if (serviceId is null)
            {
                continue;
            }

            if (!services.TryGetValue(device.Address, out var list))
            {
                list = new List<string>();
                services[device.Address] = list;
            }

            if (!list.Contains(serviceId, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(serviceId);
            }
        }

        return services.ToDictionary(
            pair => pair.Key,
            pair => new DeviceProfiles(pair.Key, pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static string? ExtractServiceId(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        var open = instanceId.IndexOf('{');
        if (open < 0 || open + 9 > instanceId.Length)
        {
            return null;
        }

        // 0000110E-... - the meaningful part is characters 4 to 8.
        var guidStart = instanceId.AsSpan(open + 1);
        if (guidStart.Length < 9 || guidStart[8] != '-')
        {
            return null;
        }

        var shortForm = guidStart[4..8].ToString();
        foreach (var c in shortForm)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return shortForm.ToUpperInvariant();
    }

    internal static string ExplainMissingBattery(DeviceProfiles? profiles, bool isConnected)
    {
        if (profiles is null)
        {
            return isConnected
                ? "the device is connected, but no service nodes were found for it yet - Windows may not have finished enumerating its profiles, or the connected profile does not expose one."
                : "no service nodes found for this device - it is probably not connected at all.";
        }

        if (profiles.CanReportBattery)
        {
            return "a battery-capable profile IS connected " +
                   $"({profiles.Describe()}), so the device itself is not reporting a level. " +
                   "That is a device firmware limitation, not a Windows one.";
        }

        return $"connected profiles are {profiles.Describe()} - none of which can carry a battery level. " +
               "On classic Bluetooth, Windows reads battery only from the Hands-Free Profile (HFP); " +
               "A2DP and AVRCP, which carry music and track controls, have no battery capability at all. " +
               "Connect the device's \"Hands-Free AG\" endpoint in Sound settings and re-check.";
    }
}
