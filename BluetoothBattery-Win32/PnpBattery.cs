using System.Runtime.InteropServices;
using System.Text;

namespace BluetoothBattery;

internal static class PnpBattery
{
    // GUID_DEVCLASS_BLUETOOTH - the Bluetooth device setup class.
    private static readonly Guid BluetoothDeviceClass =
        new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    // DEVPKEY_Bluetooth_Battery. Value is a single byte, 0-100.
    private static DEVPROPKEY BatteryProperty = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2,
    };

    // DEVPKEY_NAME - friendly name, for diagnostics only.
    private static DEVPROPKEY NameProperty = new()
    {
        fmtid = new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"),
        pid = 10,
    };

    // DEVPKEY_Device_ContainerId - same key ContainerBattery.cs reads.
    private static DEVPROPKEY ContainerIdProperty = new()
    {
        fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        pid = 2,
    };

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;
    private const uint DEVPROP_TYPE_GUID = 0x00000010;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    internal sealed record PnpDevice(
        string InstanceId, string? Name, string? Address, int? Battery, string? ContainerId);

    internal static Dictionary<string, int> ReadBatteryByAddress()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in Enumerate())
        {
            if (device.Address is not null && device.Battery is { } level &&
                !result.ContainsKey(device.Address))
            {
                result[device.Address] = level;
            }
        }

        return result;
    }

    internal static Dictionary<string, string> ReadContainerIdsByAddress()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in Enumerate())
        {
            if (device.Address is not null && device.ContainerId is not null &&
                !result.ContainsKey(device.Address))
            {
                result[device.Address] = device.ContainerId;
            }
        }

        return result;
    }

    internal static IReadOnlyList<PnpDevice> Enumerate()
    {
        var devices = new List<PnpDevice>();
        var classGuid = Guid.Empty;

        var deviceInfoSet = SetupDiGetClassDevsW(
            ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
        {
            Diagnostics.Write($"SetupDiGetClassDevs failed (Win32 {Marshal.GetLastWin32Error()}).");
            return devices;
        }

        try
        {
            var info = new SP_DEVINFO_DATA();
            info.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_NO_MORE_ITEMS)
                    {
                        Diagnostics.Write($"SetupDiEnumDeviceInfo stopped at {index} " +
                                          $"(Win32 {Marshal.GetLastWin32Error()}).");
                    }

                    break;
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref info);

                var address = ExtractAddress(instanceId);
                if (address is null)
                {
                    continue;
                }

                devices.Add(new PnpDevice(
                    instanceId,
                    ReadStringProperty(deviceInfoSet, ref info, ref NameProperty),
                    address,
                    ReadByteProperty(deviceInfoSet, ref info, ref BatteryProperty),
                    ReadGuidProperty(deviceInfoSet, ref info, ref ContainerIdProperty)));
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"PnP enumeration failed: {ex.Message}");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return devices;
    }

    private static int? ReadByteProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key)
    {
        var buffer = new byte[4];

        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, buffer,
                                       (uint)buffer.Length, out _, 0))
        {
            return null;
        }

        if (type != DEVPROP_TYPE_BYTE)
        {
            return null;
        }

        int level = buffer[0];
        return level is >= 0 and <= 100 ? level : null;
    }

    private static string? ReadStringProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref info, ref key, out _, null, 0, out var required, 0);
        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, buffer,
                                       required, out _, 0) ||
            type != DEVPROP_TYPE_STRING)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string? ReadGuidProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key)
    {
        var buffer = new byte[16];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, buffer,
                                       (uint)buffer.Length, out _, 0) ||
            type != DEVPROP_TYPE_GUID)
        {
            return null;
        }

        return new Guid(buffer).ToString("B");
    }

    private static string ReadInstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new StringBuilder(1024);
        return SetupDiGetDeviceInstanceIdW(set, ref info, buffer, (uint)buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    // Two instance id shapes carry an address:
    //   Parent device node:  BTHENUM\DEV_9847448669CB\...           - 12 hex digits after "DEV_".
    //   Per-service node:    BTHENUM\{0000110E-...}_LOCALMFG&0002\8&287335C0&0&9847448669CB_C00000000
    //                        - no "DEV_"; the 12 hex digits are in the last path segment.
    // Returned colon-separated and lowercase, matching WinRT.
    internal static string? ExtractAddress(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        const string marker = "DEV_";
        var start = instanceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && TryReadHex(instanceId, start + marker.Length, out var fromDev))
        {
            return fromDev;
        }

        var lastSegment = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        var ampersand = lastSegment.LastIndexOf("&0&", StringComparison.Ordinal);
        if (ampersand >= 0 && TryReadHex(lastSegment, ampersand + 3, out var fromService))
        {
            return fromService;
        }

        return null;
    }

    private static bool TryReadHex(string text, int start, out string? address)
    {
        address = null;

        if (start < 0 || start + 12 > text.Length)
        {
            return false;
        }

        var hex = text.Substring(start, 12);
        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        var formatted = new StringBuilder(17);
        for (var i = 0; i < 12; i += 2)
        {
            if (i > 0)
            {
                formatted.Append(':');
            }

            formatted.Append(char.ToLowerInvariant(hex[i]));
            formatted.Append(char.ToLowerInvariant(hex[i + 1]));
        }

        address = formatted.ToString();
        return true;
    }

    internal static string NormalizeAddress(string address) =>
        address.Replace("-", ":").ToLowerInvariant();
}
