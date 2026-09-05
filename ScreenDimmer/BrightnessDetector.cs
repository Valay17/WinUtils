using System.Runtime.InteropServices;

namespace ScreenDimmer;

internal sealed class BrightnessDetector : IDisposable
{
    private static readonly Guid VideoBrightnessGuid =
        new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    private IntPtr registration_;
    private bool disposed_;

    private volatile int level_ = -1;

    internal int Level => level_;

    internal bool HasReading => level_ >= 0;

    internal bool AtMinimum => level_ == 0;

    internal event Action<int>? LevelChanged;

    internal bool Register(IntPtr windowHandle)
    {
        var guid = VideoBrightnessGuid;
        registration_ = RegisterPowerSettingNotification(windowHandle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);

        if (registration_ == IntPtr.Zero)
        {
            Diagnostics.Write(
                $"RegisterPowerSettingNotification failed (Win32 {Marshal.GetLastWin32Error()}). " +
                "System brightness cannot be tracked, so brightness-key handover is unavailable. " +
                "The tray slider still works.");
            return false;
        }

        Diagnostics.Write("Registered for brightness notifications. Waiting for the first report - " +
                          "on many laptops this arrives only when brightness actually changes, so " +
                          "press a brightness key to confirm it is working.");
        return true;
    }

    internal void HandlePowerSetting(IntPtr settingPointer)
    {
        if (settingPointer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(settingPointer);

            if (setting.PowerSetting != VideoBrightnessGuid || setting.DataLength < 1)
            {
                return;
            }

            int value = setting.Data;
            if (value == level_)
            {
                return;
            }

            level_ = value;
            Diagnostics.Write($"System brightness now {value}%{(value == 0 ? " - at minimum, software dim can take over" : string.Empty)}");
            LevelChanged?.Invoke(value);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not read the brightness notification: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;

        if (registration_ != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(registration_);
            registration_ = IntPtr.Zero;
        }
    }
}
