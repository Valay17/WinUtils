using System.Text;

namespace ScreenDimmer;

internal static class Diagnostics
{
    private static readonly string LogDirectory = ResolveLogDirectory();
    private static readonly string LogPath = Path.Combine(LogDirectory, "ScreenDimmer.log");

    private static readonly bool LoggingEnabled = File.Exists(
        Path.Combine(LogDirectory, "logging.on"));

    private static readonly object Gate = new();

    private static string ResolveLogDirectory()
    {
        try
        {
            var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(executableDirectory))
            {
                return executableDirectory;
            }
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    internal static void Write(string message)
    {
        if (!LoggingEnabled)
        {
            return;
        }

        var flattened = string.Join(
            " ",
            message.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {flattened}";

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
