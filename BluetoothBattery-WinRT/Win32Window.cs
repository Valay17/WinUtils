using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BluetoothBattery;

// A real Win32 window with a managed WndProc, and the base of every window
// here. No delegate is marshalled to Windows: StaticWindowProcedure is
// [UnmanagedCallersOnly], a genuine static native entry point with a fixed
// address, so there is nothing for the GC to collect out from under it.
// Instance dispatch happens by looking the window up by handle. Exceptions
// must not cross this boundary - one escaping an [UnmanagedCallersOnly]
// method terminates the process immediately - so every call is wrapped and a
// fault is logged and swallowed.
internal abstract unsafe class Win32Window : IDisposable
{
    // No lock: every window is created on, and every message dispatched to,
    // the single UI thread.
    private static readonly Dictionary<IntPtr, Win32Window> Living = new();

    private static readonly HashSet<string> RegisteredClasses = new(StringComparer.Ordinal);

    private bool disposed_;

    protected Win32Window(
        string className,
        uint style,
        uint exStyle,
        int x = 0,
        int y = 0,
        int width = 0,
        int height = 0)
    {
        EnsureClassRegistered(className);

        fixed (char* name = className)
        {
            Handle = Win32.CreateWindowExW(
                exStyle, name, name, style,
                x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        }

        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed for '{className}' (Win32 {Marshal.GetLastWin32Error()}).");
        }

        // Registered after creation, so the messages Windows sends during
        // CreateWindowExW go to DefWindowProc; nothing here needs them.
        Living[Handle] = this;
    }

    internal IntPtr Handle { get; private set; }

    protected abstract bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr StaticWindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (Living.TryGetValue(window, out var self) &&
                self.WndProc(message, wParam, lParam, out var result))
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Window procedure threw for message 0x{message:X4}: {ex}");
        }

        return Win32.DefWindowProcW(window, message, wParam, lParam);
    }

    private static void EnsureClassRegistered(string className)
    {
        if (!RegisteredClasses.Add(className))
        {
            return;
        }

        fixed (char* name = className)
        {
            var windowClass = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)
                    &StaticWindowProcedure,
                hInstance = Win32.GetModuleHandleW(null),
                hCursor = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IDC_ARROW),
                lpszClassName = name,
            };

            if (Win32.RegisterClassExW(in windowClass) == 0)
            {
                RegisteredClasses.Remove(className);
                throw new InvalidOperationException(
                    $"RegisterClassExW failed for '{className}' (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    public virtual void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        if (Handle != IntPtr.Zero)
        {
            Living.Remove(Handle);
            Win32.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
