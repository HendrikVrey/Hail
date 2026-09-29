using System.Collections.Concurrent;

namespace Hail.Windows;

/// <summary>
/// One background thread in a single-threaded apartment, running work in the order it was
/// queued. The shell's objects (the AppsFolder, icon extraction) are apartment-threaded, so
/// they live here: off the UI thread, and never on a thread-pool thread in the multithreaded
/// apartment, where each call would be marshalled or refused.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the worker. Work cancelled before it starts never
    /// runs; once started it runs to the end, since a COM call cannot be interrupted.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
#pragma warning disable CA1031 // The exception is not swallowed: it is handed to the awaiting caller.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                completion.TrySetException(ex);
            }
        }, CancellationToken.None);

        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();

        // A shell call that never returns keeps the thread; it is a background thread, so it
        // does not keep the process, and the queue is left to it rather than disposed under it.
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _queue.Dispose();
        }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
