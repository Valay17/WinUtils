namespace ScreenDimmer;

internal static class DarkModeExperiment
{
    private const int SetPreferredAppModeOrdinal = 135;
    private const int FlushMenuThemesOrdinal = 136;

    // 0 = Default, 1 = AllowDark, 2 = ForceDark, 3 = ForceLight, 4 = Max.
    private const int AllowDark = 1;

    internal static unsafe bool TryEnable()
    {
        var module = IntPtr.Zero;

        try
        {
            module = Win32.LoadLibraryExW(
                "uxtheme.dll", IntPtr.Zero, Win32.LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (module == IntPtr.Zero)
            {
                Diagnostics.Write("Dark-mode experiment: could not load uxtheme.dll.");
                return false;
            }

            var setModeAddr = Win32.GetProcAddress(module, new IntPtr(SetPreferredAppModeOrdinal));
            var flushAddr = Win32.GetProcAddress(module, new IntPtr(FlushMenuThemesOrdinal));

            if (setModeAddr == IntPtr.Zero || flushAddr == IntPtr.Zero)
            {
                Diagnostics.Write(
                    "Dark-mode experiment: ordinal 135 or 136 not found in uxtheme.dll on this build " +
                    "- undocumented, so this was always a real possibility, not a bug.");
                return false;
            }

            var setMode = (delegate* unmanaged[Stdcall]<int, int>)setModeAddr;
            var flush = (delegate* unmanaged[Stdcall]<void>)flushAddr;

            var previous = setMode(AllowDark);
            flush();

            Diagnostics.Write(
                $"Dark-mode experiment: SetPreferredAppMode(AllowDark) called, previous mode was {previous}. " +
                "If the menu still looks light, this build's classic menus don't follow it - not fixable from here.");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Dark-mode experiment failed: {ex.Message}");
            return false;
        }
        finally
        {
            _ = module;
        }
    }
}
