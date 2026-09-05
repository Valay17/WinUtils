using System.Diagnostics;

namespace ScreenDimmer;

internal sealed class InversionWatch : IDisposable
{
    private const string InverterProcessName = "ColorInvertWindow";

    private readonly Action onDiedUnexpectedly_;

    private Process? watched_;
    private bool disposed_;

    internal InversionWatch(Action onDiedUnexpectedly)
    {
        onDiedUnexpectedly_ = onDiedUnexpectedly;
    }

    internal void Start()
    {
        if (disposed_ || watched_ is not null)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessesByName(InverterProcessName).FirstOrDefault();
            if (process is null)
            {
                Diagnostics.Write(
                    "Inversion reported on, but no Color Invert Window process is running - reverting overlays to black.");
                onDiedUnexpectedly_();
                return;
            }

            process.EnableRaisingEvents = true;
            process.Exited += OnWatchedExited;
            watched_ = process;

            if (process.HasExited)
            {
                OnWatchedExited(process, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not watch Color Invert Window for unexpected exit: {ex.Message}");
        }
    }

    internal void Stop()
    {
        var process = watched_;
        watched_ = null;

        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= OnWatchedExited;
            process.EnableRaisingEvents = false;
            process.Dispose();
        }
        catch
        {
        }
    }

    private void OnWatchedExited(object? sender, EventArgs e)
    {
        if (watched_ is null)
        {
            return;
        }

        Stop();

        Diagnostics.Write(
            "Color Invert Window exited without clearing inversion - it was most likely killed. " +
            "Reverting overlays to black so dimming does not invert a screen that no longer is.");

        onDiedUnexpectedly_();
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        Stop();
    }
}
