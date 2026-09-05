using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenDimmer;

internal readonly record struct MonitorInfo(string DeviceName, Win32.RECT Bounds, bool IsPrimary);

internal static unsafe class Monitors
{
    private static List<MonitorInfo>? collecting_;

    internal static IReadOnlyList<MonitorInfo> All()
    {
        var found = new List<MonitorInfo>();
        collecting_ = found;

        try
        {
            var callback = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32.RECT*, IntPtr, int>)
                &EnumerateCallback;

            Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        finally
        {
            collecting_ = null;
        }

        found.Sort(static (left, right) =>
            string.CompareOrdinal(left.DeviceName, right.DeviceName));

        return found;
    }

    internal static MonitorInfo? UnderCursor()
    {
        if (!Win32.GetCursorPos(out var point))
        {
            return null;
        }

        var handle = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        return Describe(handle);
    }

    private static int LengthOf(char* text, int capacity)
    {
        var length = 0;
        while (length < capacity && text[length] != '\0')
        {
            length++;
        }

        return length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumerateCallback(IntPtr monitor, IntPtr dc, Win32.RECT* clip, IntPtr data)
    {
        try
        {
            if (collecting_ is { } target && Describe(monitor) is { } info)
            {
                target.Add(info);
            }
        }
        catch
        {
        }

        return 1;
    }

    private static MonitorInfo? Describe(IntPtr monitor)
    {
        var info = new Win32.MONITORINFOEXW
        {
            cbSize = (uint)sizeof(Win32.MONITORINFOEXW),
        };

        if (!Win32.GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var device = info.szDevice;
        var name = new string(device, 0, LengthOf(device, 32));

        if (!name.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var raw = new System.Text.StringBuilder();
            var bytes = (byte*)device;
            for (var i = 0; i < 24; i++)
            {
                raw.Append(bytes[i].ToString("X2")).Append(' ');
            }

            Diagnostics.Write(
                $"Monitor name read as '{name}' (length {name.Length}), which is not a device name. " +
                $"cbSize sent {info.cbSize}, struct size {sizeof(Win32.MONITORINFOEXW)}. " +
                $"szDevice first 24 bytes: {raw.ToString().TrimEnd()}");

            name = $@"\\.\AT+{info.rcMonitor.Left}+{info.rcMonitor.Top}";
        }

        return new MonitorInfo(name, info.rcMonitor, (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0);
    }
}
