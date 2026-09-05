using System.Runtime.InteropServices;

namespace BluetoothBattery;

internal static unsafe class BluetoothRadioManager
{
    // Bluetooth Radio Media Provider coclass.
    private static readonly Guid BluetoothRadioManagerClsid = new("afd198ac-5f30-4e89-a789-5ddf60a69366");

    // IID_IMediaRadioManager, from RadioMgr.idl.
    private static readonly Guid IID_IMediaRadioManager = new("6CFDCAB5-FC47-42A5-9241-074B58830E73");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    // COM vtable slot indices, from RadioMgr.idl's method order (0-based, including IUnknown's 3 slots).
    private const int Slot_IUnknown_Release = 2;
    private const int Slot_IMediaRadioManager_GetRadioInstances = 3;
    private const int Slot_IRadioInstanceCollection_GetCount = 3;
    private const int Slot_IRadioInstanceCollection_GetAt = 4;
    private const int Slot_IRadioInstance_GetRadioState = 6;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    // DEVICE_RADIO_STATE, verbatim from RadioMgr.idl.
    private enum DeviceRadioState
    {
        RadioOn = 0,
        SoftwareOff = 1,
        HardwareOff = 2,
        SoftwareAndHardwareOff = 3,
        HardwareOnUncontrollable = 4,
        Invalid = 5,
        HardwareOffUncontrollable = 6,
    }

    private static IntPtr _manager = IntPtr.Zero;
    private static bool _managerCreationFailed;

    private static IntPtr VTableSlot(IntPtr comObject, int slot)
    {
        var vtable = *(IntPtr*)comObject;
        return *(IntPtr*)(vtable + slot * IntPtr.Size);
    }

    private static void ReleaseIfAny(IntPtr comObject)
    {
        if (comObject == IntPtr.Zero)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)VTableSlot(comObject, Slot_IUnknown_Release);
        release(comObject);
    }

    private static IntPtr GetManager()
    {
        if (_manager != IntPtr.Zero || _managerCreationFailed)
        {
            return _manager;
        }

        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);

        var clsid = BluetoothRadioManagerClsid;
        var riid = IID_IMediaRadioManager;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref riid, out var manager);
        if (hr < 0 || manager == IntPtr.Zero)
        {
            Diagnostics.Write($"Bluetooth radio manager unavailable (hr=0x{hr:X8}) - using devnode count instead.");
            _managerCreationFailed = true;
            return IntPtr.Zero;
        }

        _manager = manager;
        return _manager;
    }

    internal static RadioState? TryReadState()
    {
        var collection = IntPtr.Zero;
        var instance = IntPtr.Zero;

        try
        {
            var manager = GetManager();
            if (manager == IntPtr.Zero)
            {
                return null;
            }

            var getRadioInstances =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTableSlot(manager, Slot_IMediaRadioManager_GetRadioInstances);
            var hr = getRadioInstances(manager, &collection);

            if (hr < 0 || collection == IntPtr.Zero)
            {
                Diagnostics.Write($"Bluetooth radio manager returned no instance collection (hr=0x{hr:X8}).");
                return null;
            }

            var getCount =
                (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VTableSlot(collection, Slot_IRadioInstanceCollection_GetCount);
            uint count;
            hr = getCount(collection, &count);
            if (hr < 0)
            {
                Diagnostics.Write($"Bluetooth radio manager instance count failed (hr=0x{hr:X8}).");
                return null;
            }

            var getAt =
                (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTableSlot(collection, Slot_IRadioInstanceCollection_GetAt);

            for (var i = 0u; i < count; i++)
            {
                hr = getAt(collection, i, &instance);

                if (hr < 0 || instance == IntPtr.Zero)
                {
                    continue;
                }

                var getRadioState =
                    (delegate* unmanaged[Stdcall]<IntPtr, DeviceRadioState*, int>)VTableSlot(instance, Slot_IRadioInstance_GetRadioState);
                DeviceRadioState state;
                hr = getRadioState(instance, &state);

                ReleaseIfAny(instance);
                instance = IntPtr.Zero;

                if (hr < 0)
                {
                    continue;
                }

                if (state is DeviceRadioState.HardwareOnUncontrollable or DeviceRadioState.HardwareOffUncontrollable)
                {
                    Diagnostics.Write($"Bluetooth radio manager returned an untested state: {state}.");
                }

                var mapped = state switch
                {
                    DeviceRadioState.RadioOn or DeviceRadioState.HardwareOnUncontrollable => RadioState.On,
                    DeviceRadioState.SoftwareOff or DeviceRadioState.HardwareOff
                        or DeviceRadioState.SoftwareAndHardwareOff or DeviceRadioState.HardwareOffUncontrollable
                        => RadioState.Off,
                    _ => (RadioState?)null,
                };

                if (mapped is not null)
                {
                    Diagnostics.Write($"Bluetooth radio state via radio manager: {mapped} (raw {state}).");
                    return mapped;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Diagnostics.Write($"Bluetooth radio manager unavailable ({ex.GetType().Name}: {ex.Message}) - using devnode count instead.");
            return null;
        }
        finally
        {
            ReleaseIfAny(instance);
            ReleaseIfAny(collection);
        }
    }
}
