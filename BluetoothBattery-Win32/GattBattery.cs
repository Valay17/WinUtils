using System.Runtime.InteropServices;
using System.Text;

namespace BluetoothBattery;

internal static class GattBattery
{
    // GUID_BLUETOOTHLE_DEVICE_INTERFACE.
    private static readonly Guid BleDeviceInterface = new("781aee18-7733-4ce4-add0-091f4ddd3319");

    private const ushort BatteryServiceUuid = 0x180F;
    private const ushort BatteryLevelCharacteristicUuid = 0x2A19;

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    // BTH_LE_UUID: BOOLEAN IsShortUuid, followed by a union of USHORT ShortUuid /
    // GUID LongUuid. Native size is 20 bytes (BOOLEAN padded to 4-byte alignment
    // ahead of the 16-byte GUID union member).
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct BTH_LE_UUID
    {
        // Native BOOLEAN is one byte, not the four-byte BOOL a plain C# bool marshals to by default.
        [FieldOffset(0)]
        [MarshalAs(UnmanagedType.U1)]
        public bool IsShortUuid;

        [FieldOffset(4)]
        public ushort ShortUuid;

        [FieldOffset(4)]
        public Guid LongUuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BTH_LE_GATT_SERVICE
    {
        public BTH_LE_UUID ServiceUuid;
        public ushort AttributeHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BTH_LE_GATT_CHARACTERISTIC
    {
        public ushort ServiceHandle;
        public BTH_LE_UUID CharacteristicUuid;
        public ushort AttributeHandle;
        public ushort CharacteristicValueHandle;

        // All eight of these are native BOOLEAN (1 byte), same as BTH_LE_UUID.IsShortUuid above.
        [MarshalAs(UnmanagedType.U1)] public bool IsBroadcastable;
        [MarshalAs(UnmanagedType.U1)] public bool IsReadable;
        [MarshalAs(UnmanagedType.U1)] public bool IsWritable;
        [MarshalAs(UnmanagedType.U1)] public bool IsWritableWithoutResponse;
        [MarshalAs(UnmanagedType.U1)] public bool IsSignedWritable;
        [MarshalAs(UnmanagedType.U1)] public bool IsNotifiable;
        [MarshalAs(UnmanagedType.U1)] public bool IsIndicatable;
        [MarshalAs(UnmanagedType.U1)] public bool HasExtendedProperties;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("bluetoothapis.dll")]
    private static extern int BluetoothGATTGetServices(
        IntPtr hDevice, ushort servicesBufferCount, [In, Out] BTH_LE_GATT_SERVICE[]? servicesBuffer,
        out ushort servicesBufferActual, uint flags);

    [DllImport("bluetoothapis.dll")]
    private static extern int BluetoothGATTGetCharacteristics(
        IntPtr hDevice, ref BTH_LE_GATT_SERVICE service, ushort charBufferCount,
        [In, Out] BTH_LE_GATT_CHARACTERISTIC[]? charBuffer, out ushort charBufferActual, uint flags);

    [DllImport("bluetoothapis.dll")]
    private static extern int BluetoothGATTGetCharacteristicValue(
        IntPtr hDevice, ref BTH_LE_GATT_CHARACTERISTIC characteristic, uint valueBufferSize,
        IntPtr valueBuffer, out ushort valueBufferActual, uint flags);

    internal static int? TryReadBattery(string normalizedAddress)
    {
        var devicePath = FindDeviceInterfacePath(normalizedAddress);
        if (devicePath is null)
        {
            return null;
        }

        var handle = CreateFileW(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle == InvalidHandle)
        {
            Diagnostics.Write($"GATT: could not open {devicePath} (Win32 {Marshal.GetLastWin32Error()}).");
            return null;
        }

        try
        {
            return ReadBatteryLevel(handle);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"GATT read failed for {normalizedAddress}: {ex.Message}");
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int? ReadBatteryLevel(IntPtr handle)
    {
        var hr = BluetoothGATTGetServices(handle, 0, null, out var serviceCount, 0);
        if (serviceCount == 0)
        {
            return null;
        }

        var services = new BTH_LE_GATT_SERVICE[serviceCount];
        hr = BluetoothGATTGetServices(handle, serviceCount, services, out serviceCount, 0);
        if (hr != 0)
        {
            return null;
        }

        var battery = services.Where(s => s.ServiceUuid.IsShortUuid && s.ServiceUuid.ShortUuid == BatteryServiceUuid)
            .Cast<BTH_LE_GATT_SERVICE?>()
            .FirstOrDefault();

        if (battery is not { } service)
        {
            return null;
        }

        hr = BluetoothGATTGetCharacteristics(handle, ref service, 0, null, out var charCount, 0);
        if (charCount == 0)
        {
            return null;
        }

        var characteristics = new BTH_LE_GATT_CHARACTERISTIC[charCount];
        hr = BluetoothGATTGetCharacteristics(handle, ref service, charCount, characteristics, out charCount, 0);
        if (hr != 0)
        {
            return null;
        }

        var levelChar = characteristics
            .Where(c => c.CharacteristicUuid.IsShortUuid && c.CharacteristicUuid.ShortUuid == BatteryLevelCharacteristicUuid)
            .Cast<BTH_LE_GATT_CHARACTERISTIC?>()
            .FirstOrDefault();

        if (levelChar is not { } characteristic)
        {
            return null;
        }

        // The buffer is a BTH_LE_GATT_VALUE: a 4-byte ULONG DataSize header followed
        // by the UCHAR Data[] payload - the battery level is read at offset 4.
        const int valueHeaderSize = 4;
        const int valueBufferSize = 8;
        var valueBuffer = Marshal.AllocHGlobal(valueBufferSize);
        try
        {
            hr = BluetoothGATTGetCharacteristicValue(
                handle, ref characteristic, valueBufferSize, valueBuffer, out var actual, 0);
            if (hr != 0 || actual < valueHeaderSize + 1)
            {
                return null;
            }

            var level = Marshal.ReadByte(valueBuffer, valueHeaderSize);
            return level is >= 0 and <= 100 ? level : null;
        }
        finally
        {
            Marshal.FreeHGlobal(valueBuffer);
        }
    }

    private static string? FindDeviceInterfacePath(string normalizedAddress)
    {
        var interfaceGuid = BleDeviceInterface;
        var deviceInfoSet = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA();
            interfaceData.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

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
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 6 on x64 (4-byte
                    // cbSize field + start of the WCHAR array, packed) - a documented
                    // Windows-SDK oddity, not a typo.
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                    if (!SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, detailBuffer,
                            required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                    if (path is not null &&
                        path.Contains(normalizedAddress.Replace(":", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
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

        return null;
    }
}
