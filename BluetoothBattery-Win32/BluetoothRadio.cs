using System.Runtime.InteropServices;

namespace BluetoothBattery;

internal sealed partial class BluetoothRadio : IDisposable
{
    // GUID_DEVCLASS_BLUETOOTH.
    private static readonly Guid BluetoothClassGuid = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    private static int CountBluetoothClassDevnodes()
    {
        var classGuid = BluetoothClassGuid;
        var deviceInfoSet = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var count = 0;
            for (uint index = 0; ; index++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref devInfo))
                {
                    break;
                }

                count++;
            }

            return count;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    // GUID_BLUETOOTH_RADIO_IN_RANGE - a paired device came within range (connected).
    internal static readonly Guid RadioInRange = new("ea3b5b82-26ee-450e-b0d8-d26fe30a3869");

    // GUID_BLUETOOTH_RADIO_OUT_OF_RANGE - a paired device went out of range (disconnected).
    internal static readonly Guid RadioOutOfRange = new("e28867c9-c2b6-4e8d-95a7-3299ce7a2c1e");

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        internal uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_RADIO_INFO
    {
        internal uint dwSize;
        internal ulong address; // BLUETOOTH_ADDRESS is a 6-byte address in an 8-byte union; low 48 bits.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        internal string szName;
        internal uint ulClassofDevice;
        internal ushort lmpSubversion;
        internal ushort manufacturer;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(
        ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl")]
    private static extern int BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO pRadioInfo);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // GUID_BTHPORT_DEVICE_INTERFACE.
    private static readonly Guid BluetoothPortDeviceInterfaceGuid = new("0850302a-b344-4fda-9be9-90576b8d46f0");

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
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

    private static IntPtr OpenRadioHandleViaSetupApi()
    {
        var classGuid = BluetoothPortDeviceInterfaceGuid;
        var deviceInfoSet = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };

            if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref classGuid, 0, ref interfaceData))
            {
                return IntPtr.Zero;
            }

            SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0,
                out var required, IntPtr.Zero);
            if (required == 0)
            {
                return IntPtr.Zero;
            }

            var detailBuffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W - 6 on x86, 8 on x64.
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                if (!SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, detailBuffer,
                        required, out _, IntPtr.Zero))
                {
                    return IntPtr.Zero;
                }

                var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                if (path is null)
                {
                    return IntPtr.Zero;
                }

                var fileHandle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                return fileHandle == InvalidHandle ? IntPtr.Zero : fileHandle;
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private IntPtr _radioHandle = IntPtr.Zero;
    private bool _disposed;

    internal static Task<BluetoothRadio?> OpenAsync()
    {
        var findParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        var radioHandle = IntPtr.Zero;

        var findHandle = BluetoothFindFirstRadio(ref findParams, out radioHandle);
        var win32Found = findHandle != IntPtr.Zero;
        if (win32Found)
        {
            BluetoothFindRadioClose(findHandle);
        }
        else
        {
            radioHandle = OpenRadioHandleViaSetupApi();
            Diagnostics.Write(radioHandle != IntPtr.Zero
                ? "BluetoothFindFirstRadio found no radio - opened one via SetupAPI/CreateFile instead."
                : $"BluetoothFindFirstRadio found no radio (Win32 {Marshal.GetLastWin32Error()}), and the " +
                  "SetupAPI fallback found none either.");
        }

        var devnodeCount = CountBluetoothClassDevnodes();

        if (!win32Found && radioHandle == IntPtr.Zero && devnodeCount == 0)
        {
            return Task.FromResult<BluetoothRadio?>(null);
        }

        return Task.FromResult<BluetoothRadio?>(new BluetoothRadio(radioHandle));
    }

    private BluetoothRadio(IntPtr radioHandle)
    {
        _radioHandle = radioHandle;
    }

    internal RadioState ReadState()
    {
        var viaRadioManager = BluetoothRadioManager.TryReadState();
        if (viaRadioManager is not null)
        {
            return viaRadioManager.Value;
        }

        var count = CountBluetoothClassDevnodes();
        return count switch
        {
            0 => RadioState.Unknown,
            1 => RadioState.Off,
            _ => RadioState.On,
        };
    }

    internal IntPtr Handle => _radioHandle;

    internal string? ReadName()
    {
        if (_radioHandle == IntPtr.Zero)
        {
            return null;
        }

        var info = new BLUETOOTH_RADIO_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_RADIO_INFO>() };
        return BluetoothGetRadioInfo(_radioHandle, ref info) == 0 ? info.szName : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_radioHandle != IntPtr.Zero)
        {
            CloseHandle(_radioHandle);
            _radioHandle = IntPtr.Zero;
        }
    }
}
