using System.Threading;

namespace BluetoothBattery;

// A named event another process can signal to ask for a clean exit.
// Registered with the thread pool rather than a dedicated thread, since it
// blocks on a kernel handle and does nothing until signaled.
internal sealed partial class ShutdownSignal : IDisposable
{
    private readonly EventWaitHandle? handle_;
    private readonly RegisteredWaitHandle? registration_;
    private bool disposed_;

    internal ShutdownSignal(string name, Action onSignalled)
    {
        try
        {
            // Manual-reset so a signal arriving before the wait is registered
            // is not lost.
            handle_ = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{name}");

            registration_ = ThreadPool.RegisterWaitForSingleObject(
                handle_,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        onSignalled();
                    }
                },
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Could not create the shutdown event: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (disposed_)
        {
            return;
        }

        disposed_ = true;
        registration_?.Unregister(null);
        handle_?.Dispose();
    }
}
