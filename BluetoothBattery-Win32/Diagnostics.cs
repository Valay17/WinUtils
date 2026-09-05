using System.Text;

namespace BluetoothBattery;

internal static class Diagnostics
{
    private static readonly string LogDirectory = ResolveLogDirectory();

    private static readonly string LogPath = Path.Combine(LogDirectory, "BluetoothBattery.log");

    private static readonly bool LoggingEnabled = File.Exists(
        Path.Combine(LogDirectory, "logging.on"));

    internal static bool DiagnosticsAvailable => LoggingEnabled;

    internal static bool LoggingIsOn => LoggingEnabled;


    internal static readonly string DumpPath = Path.Combine(LogDirectory, "BluetoothBattery-diagnostics.log");

    private static readonly object Gate = new();

    private static string ResolveLogDirectory()
    {
        try
        {
            // Environment.ProcessPath, not Assembly.Location: the latter is empty in a single-file published binary.
            var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(executableDirectory))
            {
                return executableDirectory;
            }
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    internal static void Write(string message)
    {
        if (!LoggingEnabled)
        {
            return;
        }

        var flattened = string.Join(
            " ",
            message.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {flattened}";

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    internal static void WriteDeviceDump(IEnumerable<RawDeviceInfo> devices, IEnumerable<DeviceView> known)
    {
        var report = new StringBuilder();
        report.AppendLine($"BTBattery device diagnostics - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        Dictionary<string, BluetoothProfiles.DeviceProfiles> profiles;
        try
        {
            profiles = BluetoothProfiles.ScanByAddress();
        }
        catch (Exception ex)
        {
            report.AppendLine($"(profile scan failed: {ex.Message})");
            profiles = new Dictionary<string, BluetoothProfiles.DeviceProfiles>();
        }

        report.AppendLine("== As this utility sees them ==");
        var any = false;
        foreach (var device in known)
        {
            any = true;

            BluetoothProfiles.DeviceProfiles? deviceProfiles = null;
            if (device.Address is not null)
            {
                profiles.TryGetValue(PnpBattery.NormalizeAddress(device.Address), out deviceProfiles);
            }

            report.AppendLine($"  {device.Name}");
            report.AppendLine($"    Id        : {device.Id}");
            report.AppendLine($"    Battery   : {device.BatteryText} (source: {device.Source})");
            report.AppendLine($"    Connected : {device.IsConnected}");
            report.AppendLine($"    Profiles  : {deviceProfiles?.Describe() ?? "(none found)"}");
            report.AppendLine($"    Can report battery : {(deviceProfiles?.CanReportBattery ?? false)}");
            report.AppendLine($"    Updated   : {device.LastUpdatedText}");

            if (device.BatteryPercent is null)
            {
                report.AppendLine($"    Why       : {BluetoothProfiles.ExplainMissingBattery(deviceProfiles, device.IsConnected)}");
            }
        }

        if (!any)
        {
            report.AppendLine("  (none)");
        }

        report.AppendLine();
        report.AppendLine("== PnP device tree (SetupAPI) ==");
        report.AppendLine("The source Windows Settings itself uses for Bluetooth battery.");
        report.AppendLine();

        try
        {
            var pnpDevices = PnpBattery.Enumerate();
            if (pnpDevices.Count == 0)
            {
                report.AppendLine("  (no Bluetooth class devices found)");
            }

            foreach (var device in pnpDevices)
            {
                report.AppendLine($"  {device.Name ?? "(unnamed)"}");
                report.AppendLine($"    InstanceId : {device.InstanceId}");
                report.AppendLine($"    Address    : {device.Address ?? "(not derivable)"}");
                report.AppendLine($"    Battery    : {(device.Battery.HasValue ? device.Battery + "%" : "(not reported)")}");
                report.AppendLine();
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  PnP enumeration failed: {ex.Message}");
        }

        report.AppendLine();
        report.AppendLine("== Device containers ==");
        report.AppendLine("The object Windows Settings groups a device under. Not a device node,");
        report.AppendLine("so the SetupAPI sweep above cannot see these.");
        report.AppendLine();

        try
        {
            var containers = ContainerBattery.EnumerateAsync().GetAwaiter().GetResult();
            if (containers.Count == 0)
            {
                report.AppendLine("  (none enumerated)");
            }

            foreach (var container in containers)
            {
                report.AppendLine($"  {container.Name ?? "(unnamed)"}");
                report.AppendLine($"    Id        : {container.Id}");
                report.AppendLine($"    Connected : {container.Connected?.ToString() ?? "(unknown)"}");
                report.AppendLine($"    Battery   : {(container.Battery.HasValue ? container.Battery + "%" : "(not reported)")}");
                if (container.Source is not null)
                {
                    report.AppendLine($"    Source    : {container.Source}");
                }

                report.AppendLine();
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Container enumeration failed: {ex.Message}");
        }

        report.AppendLine();
        report.AppendLine("== Raw properties reported by Windows (SetupAPI) ==");
        report.AppendLine("Same sweep as the PnP tree above, unfiltered - every property key read");
        report.AppendLine("off each node, not just DEVPKEY_Bluetooth_Battery.");
        report.AppendLine();

        foreach (var device in devices)
        {
            report.AppendLine($"  {device.Name}");
            report.AppendLine($"    Id   : {device.Id}");
            report.AppendLine($"    Kind : {device.Kind}");

            if (device.Properties.Count == 0)
            {
                report.AppendLine("    (no properties returned)");
            }
            else
            {
                foreach (var property in device.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    report.AppendLine($"    {property.Key} = {FormatValue(property.Value)}");
                }
            }

            report.AppendLine();
        }

        try
        {
            File.WriteAllText(DumpPath, report.ToString(), Encoding.UTF8);
            Write($"Wrote device diagnostics to {DumpPath}");
        }
        catch (Exception ex)
        {
            Write($"Could not write device diagnostics: {ex.Message}");
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "(null)",
        string[] array => string.Join(", ", array),
        _ => value.ToString() ?? "(null)",
    };
}
