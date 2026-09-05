using System.Collections.Concurrent;

namespace BluetoothBattery;

internal sealed class UiDispatcher
{
    private readonly IntPtr window_;
    private readonly ConcurrentQueue<Action> queue_ = new();

    internal UiDispatcher(IntPtr window)
    {
        window_ = window;
    }

    internal void Post(Action work)
    {
        queue_.Enqueue(work);

        if (!Win32.PostMessageW(window_, Win32.WM_DISPATCH, IntPtr.Zero, IntPtr.Zero))
        {
            if (queue_.TryDequeue(out _))
            {
                Diagnostics.Write("UiDispatcher: the UI window is gone; a queued callback was dropped.");
            }
        }
    }

    internal void Drain()
    {
        while (queue_.TryDequeue(out var work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Diagnostics.Write($"A dispatched callback threw: {ex}");
            }
        }
    }
}
