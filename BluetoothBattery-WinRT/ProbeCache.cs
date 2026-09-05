using System.Runtime.InteropServices;
using System.Text;

namespace BluetoothBattery;

internal sealed class ProbeCache
{
    private const string FileName = "cache.ini";
    private const string ProbeSection = "probe";

    internal string[] CachedProperties { get; set; } = Array.Empty<string>();

    internal string[] CachedBatteryProperties { get; set; } = Array.Empty<string>();

    internal static string Directory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    internal static string FilePath => Path.Combine(Directory, FileName);

    internal static ProbeCache Load()
    {
        var cache = new ProbeCache();

        try
        {
            if (!File.Exists(FilePath))
            {
                return cache;
            }

            cache.CachedProperties = ReadList(ProbeSection, "properties");
            cache.CachedBatteryProperties = ReadList(ProbeSection, "batteryProperties");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Cache load failed: {ex.Message}");
        }

        return cache;
    }

    internal void SaveProbeResult(string[] all, string[] battery)
    {
        CachedProperties = all;
        CachedBatteryProperties = battery;

        try
        {
            if (!File.Exists(FilePath))
            {
                WriteHeader();
            }

            WritePrivateProfileStringW(
                ProbeSection, "properties", string.Join(",", all), FilePath);
            WritePrivateProfileStringW(
                ProbeSection, "batteryProperties", string.Join(",", battery), FilePath);

            WritePrivateProfileStringW(null, null, null, FilePath);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not cache the probe result: {ex.Message}");
        }
    }

    private static string[] ReadList(string section, string key)
    {
        var buffer = new StringBuilder(512);
        GetPrivateProfileStringW(section, key, string.Empty, buffer, buffer.Capacity, FilePath);

        var text = buffer.ToString().Trim();
        if (text.Length == 0)
        {
            return Array.Empty<string>();
        }

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void WriteHeader()
    {
        var contents =
            $"""
            ; Bluetooth Battery - probe result cache, not a setting.
            ; Leave it alone.
            ; If every device reads N/A, delete this file; it rebuilds itself.

            """;

        File.WriteAllText(FilePath, contents, Encoding.UTF8);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileStringW(
        string section, string key, string defaultValue,
        StringBuilder returnedString, int size, string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WritePrivateProfileStringW(
        string? section, string? key, string? value, string fileName);
}
