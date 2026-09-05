using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppTiling;

#if TARGET_DESKTOP
// THE WHOLE FILE IS COMPILED OUT - never define TARGET_DESKTOP.

internal static class VDesktop
{
    private const string ShellKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const string DesktopIdsValueName = "VirtualDesktopIDs";
    private const string CurrentDesktopValueName = "CurrentVirtualDesktop";
    private const int GuidByteLength = 16;
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private static readonly Guid CLSID_VirtualDesktopManager =
        new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    // IVirtualDesktopManager vtable: 0 QueryInterface, 1 AddRef, 2 Release,
    // 3 IsWindowOnCurrentVirtualDesktop, 4 GetWindowDesktopId, 5 MoveWindowToDesktop.
    private const int VtblIsWindowOnCurrentVirtualDesktop = 3;

    private static readonly Guid IID_IVirtualDesktopManager =
        new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint CLSCTX_LOCAL_SERVER = 0x4;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr outer, uint context, in Guid riid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    private const uint COINIT_APARTMENTTHREADED = 0x2;

    private static IntPtr _manager = IntPtr.Zero;

    private static bool _managerCreationFailed;

    // Must be declared before DesktopKeyPaths: field initializers run in declaration order.
    private static readonly int CurrentSessionId = ReadSessionId();

    private static readonly string[] DesktopKeyPaths =
    {
        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{CurrentSessionId}\VirtualDesktops",
        ShellKeyPath,
    };

    private static int ReadSessionId()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read the session id: {ex.Message}");
            return 0;
        }
    }

    private static string? _lastLoggedBlobPath;

    internal static Guid? ResolveDesktopId(int oneBasedPosition)
    {
        var blob = ReadDesktopIdBlob();
        if (blob is null)
        {
            Log.Write("ERROR: could not read the virtual desktop ID list from the registry. " +
                      "Neither known registry path returned a VirtualDesktopIDs value.");
            return null;
        }

        if (blob.Length == 0 || blob.Length % GuidByteLength != 0)
        {
            Log.Write($"ERROR: VirtualDesktopIDs blob has an unexpected length ({blob.Length} bytes, " +
                      $"expected a non-zero multiple of {GuidByteLength}).");
            return null;
        }

        var desktopCount = blob.Length / GuidByteLength;
        Log.Write($"Virtual desktops found: {desktopCount}");

        if (oneBasedPosition < 1 || oneBasedPosition > desktopCount)
        {
            Log.Write($"ERROR: desktop {oneBasedPosition} does not exist - only {desktopCount} " +
                      "virtual desktop(s) are currently open. Create it in Task View and re-run.");
            return null;
        }

        var offset = (oneBasedPosition - 1) * GuidByteLength;
        var id = new Guid(blob.AsSpan(offset, GuidByteLength));
        Log.Write($"Desktop {oneBasedPosition} resolved to {id}");
        return id;
    }

    internal static int? CurrentDesktopNumber()
    {
        var blob = ReadDesktopIdBlob();
        if (blob is null || blob.Length == 0 || blob.Length % GuidByteLength != 0)
        {
            return null;
        }

        var current = ReadCurrentDesktopId();
        if (current is null)
        {
            return null;
        }

        for (var index = 0; index < blob.Length / GuidByteLength; index++)
        {
            var id = new Guid(blob.AsSpan(index * GuidByteLength, GuidByteLength));
            if (id == current.Value)
            {
                return index + 1;
            }
        }

        Log.Write($"CurrentVirtualDesktop ({current.Value}) is not in the VirtualDesktopIDs list.");
        return null;
    }

    private static Guid? ReadCurrentDesktopId()
    {
        foreach (var path in DesktopKeyPaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue(CurrentDesktopValueName) is byte[] blob && blob.Length == GuidByteLength)
                {
                    return new Guid(blob);
                }
            }
            catch (Exception ex)
            {
                Log.Write($@"Reading CurrentVirtualDesktop from HKCU\{path} failed: {ex.Message}");
            }
        }

        return null;
    }

    internal static unsafe bool? IsWindowOnCurrentDesktop(IntPtr hwnd, string label)
    {
        var manager = GetManager();
        if (manager == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // vtable[3](this, hwnd, out int onCurrent) -> HRESULT
            var vtable = *(IntPtr**)manager;
            var isOnCurrent =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)
                vtable[VtblIsWindowOnCurrentVirtualDesktop];

            int onCurrent;
            var hr = isOnCurrent(manager, hwnd, &onCurrent);

            if (hr != 0)
            {
                Log.Write($"Could not tell which desktop {label} is on (HRESULT 0x{hr:X8}).");
                return null;
            }

            return onCurrent != 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not tell which desktop {label} is on: {ex.Message}");
            return null;
        }
    }

#if MOVE_TO_DESKTOP
    // COMPILED OUT - never define MOVE_TO_DESKTOP.
    internal static bool MoveWindow(IntPtr hwnd, Guid desktopId, string label)
    {
        var manager = GetManager();
        if (manager is null)
        {
            return false;
        }

        try
        {
            var hr = manager.MoveWindowToDesktop(hwnd, ref desktopId);
            if (hr == 0)
            {
                Log.Write($"Moved {label} to desktop {desktopId}");
                return true;
            }

            if (hr == E_ACCESSDENIED)
            {
                Log.Write($"ERROR: moving {label} was denied (E_ACCESSDENIED). The documented " +
                          "MoveWindowToDesktop only moves windows owned by the calling process, so it " +
                          "cannot move another application's window. This needs the undocumented " +
                          "IVirtualDesktopManagerInternal - elevation does not help.");
            }
            else
            {
                Log.Write($"ERROR: moving {label} failed with HRESULT 0x{hr:X8}.");
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"ERROR: moving {label} threw: {ex.Message}");
            return false;
        }
    }

#endif

    private static IntPtr GetManager()
    {
        if (_manager != IntPtr.Zero || _managerCreationFailed)
        {
            return _manager;
        }

        try
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);

            var clsid = CLSID_VirtualDesktopManager;
            var iid = IID_IVirtualDesktopManager;

            var hr = CoCreateInstance(
                in clsid, IntPtr.Zero,
                CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER,
                in iid, out var instance);

            if (hr != 0 || instance == IntPtr.Zero)
            {
                Log.Write($"ERROR: creating VirtualDesktopManager failed (HRESULT 0x{hr:X8}).");
                _managerCreationFailed = true;
                return IntPtr.Zero;
            }

            _manager = instance;
            return _manager;
        }
        catch (Exception ex)
        {
            Log.Write($"ERROR: creating VirtualDesktopManager failed: {ex.Message}");
            _managerCreationFailed = true;
            return IntPtr.Zero;
        }
    }

    // Session-scoped key checked first: some Windows 10 builds scope VirtualDesktopIDs
    // to the session, newer builds use the plain Explorer key.
    private static byte[]? ReadDesktopIdBlob()
    {
        foreach (var path in DesktopKeyPaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue(DesktopIdsValueName) is byte[] blob && blob.Length > 0)
                {
                    if (_lastLoggedBlobPath != path)
                    {
                        _lastLoggedBlobPath = path;
                        Log.Write($@"Read VirtualDesktopIDs from HKCU\{path} ({blob.Length} bytes)");
                    }

                    return blob;
                }
            }
            catch (Exception ex)
            {
                Log.Write($@"Reading HKCU\{path} failed: {ex.Message}");
            }
        }

        return null;
    }
}

#endif
