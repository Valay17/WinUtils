using System.Threading;

namespace ScreenDimmer;

internal sealed class ShutdownSignal : IDisposable
{
    private readonly EventWaitHandle? handle_;
    private readonly RegisteredWaitHandle? registration_;
    private bool disposed_;

    internal ShutdownSignal(string name, Action onSignalled)
    {
        try
        {
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
