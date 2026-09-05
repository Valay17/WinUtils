using System.Runtime.InteropServices;
using System.Text;

namespace BluetoothBattery;

internal static class ContainerBattery
{
    // DEVPKEY_Device_ContainerId.
    private static DEVPROPKEY ContainerIdProperty = new()
    {
        fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        pid = 2,
    };

    // DEVPKEY_Bluetooth_Battery. Same key PnpBattery.cs reads.
    private static DEVPROPKEY BatteryProperty = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2,
    };

    private static DEVPROPKEY NameProperty = new()
    {
        fmtid = new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"),
        pid = 10,
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
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    internal sealed record ContainerInfo(string Id, string? Name, bool? Connected, int? Battery, string? Source);

    internal static Task<Dictionary<string, int>> ReadByContainerAsync()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var container in Enumerate())
        {
            if (container.Battery is { } level && !result.ContainsKey(container.Id))
            {
                result[container.Id] = level;
            }
        }

        return Task.FromResult(result);
    }

    internal static Task<IReadOnlyList<ContainerInfo>> EnumerateAsync() =>
        Task.FromResult<IReadOnlyList<ContainerInfo>>(Enumerate());

    private static List<ContainerInfo> Enumerate()
    {
        var byContainer = new Dictionary<string, (string? Name, int? Battery)>(StringComparer.OrdinalIgnoreCase);
        var classGuid = Guid.Empty;

        var deviceInfoSet = SetupDiGetClassDevsW(
            ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
        {
            Diagnostics.Write($"Container sweep: SetupDiGetClassDevs failed (Win32 {Marshal.GetLastWin32Error()}).");
            return new List<ContainerInfo>();
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
                        Diagnostics.Write($"Container sweep: enumeration stopped at {index} " +
                                          $"(Win32 {Marshal.GetLastWin32Error()}).");
                    }

                    break;
                }

                var containerId = ReadGuidProperty(deviceInfoSet, ref info, ref ContainerIdProperty);
                if (containerId is null)
                {
                    continue;
                }

                var battery = ReadByteProperty(deviceInfoSet, ref info, ref BatteryProperty);
                var name = ReadStringProperty(deviceInfoSet, ref info, ref NameProperty);

                if (!byContainer.TryGetValue(containerId, out var existing))
                {
                    byContainer[containerId] = (name, battery);
                }
                else if (existing.Battery is null && battery is not null)
                {
                    // First battery reading found for this container wins.
                    byContainer[containerId] = (existing.Name ?? name, battery);
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Container sweep failed: {ex.Message}");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        var results = byContainer
            .Select(kv => new ContainerInfo(kv.Key, kv.Value.Name, null, kv.Value.Battery,
                kv.Value.Battery.HasValue ? "DEVPKEY_Bluetooth_Battery (via child node)" : null))
            .ToList();

        Diagnostics.Write($"Enumerated {results.Count} device container(s), " +
                          $"{results.Count(c => c.Battery.HasValue)} with a battery value.");

        return results;
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

    private static int? ReadByteProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key)
    {
        var buffer = new byte[4];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, buffer,
                                       (uint)buffer.Length, out _, 0) ||
            type != DEVPROP_TYPE_BYTE)
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
}
