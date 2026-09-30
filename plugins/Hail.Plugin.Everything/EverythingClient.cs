using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static Hail.Plugin.Everything.NativeMethods;

namespace Hail.Plugin.Everything;

/// <summary>Everything, as the provider sees it; a fake in the tests.</summary>
internal interface IEverything : IDisposable
{
    /// <summary>
    /// The items Everything finds for <paramref name="search"/> in its own search syntax.
    /// Throws <see cref="EverythingUnavailableException"/> when Everything is not running or
    /// does not answer.
    /// </summary>
    Task<IReadOnlyList<EverythingItem>> SearchAsync(string search, uint flags, int maxResults, CancellationToken ct);
}

/// <summary>Everything is not there to ask, in a sentence for the box.</summary>
internal sealed class EverythingUnavailableException(string message) : Exception(message);

/// <summary>
/// Asks Everything over its IPC (<see cref="EverythingIpc"/>). Everything answers a query by
/// sending the list to a window, so the client keeps one: a hidden window on a thread of its
/// own that does nothing but wait for replies. Disposing it closes the window and ends the
/// thread, which is what lets Hail unload the plugin.
/// </summary>
internal sealed class EverythingClient : IEverything
{
    /// <summary>How long Everything is given to answer; it answers from memory, so this is generous.</summary>
    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long sending the query may block if Everything's window is busy.</summary>
    private const uint SendTimeoutMs = 1000;

    /// <summary>A reply larger than this is not a list of a hundred paths.</summary>
    private const uint MaxReplyBytes = 16 * 1024 * 1024;

    private readonly string _serverClass;
    private readonly string _ownClass = $"Hail.Everything.Reply.{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowProcedure _procedure;
    private readonly Thread _thread;
    private int _disposed;

    /// <param name="serverClass">Everything's window class; a test's own fake answers under another.</param>
    public EverythingClient(string serverClass = EverythingIpc.WindowClass)
    {
        _serverClass = serverClass;

        // Held in a field for as long as the window exists: Windows calls it through a pointer
        // the garbage collector cannot see.
        _procedure = Procedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "Everything reply window" };
        _thread.Start();
    }

    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    public async Task<IReadOnlyList<EverythingItem>> SearchAsync(string search, uint flags, int maxResults, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var window = await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        var server = FindWindow(_serverClass, null);
        if (server == 0)
        {
            throw new EverythingUnavailableException("Everything is not running.");
        }

        // An id nobody can guess, so a reply is only taken from whoever was sent the query.
        var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint id;
        do
        {
            id = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        }
        while (!_pending.TryAdd(id, reply));
        try
        {
            var query = EverythingIpc.BuildQuery((uint)window, id, flags, (uint)maxResults, search);
            if (!Send(server, window, query))
            {
                throw new EverythingUnavailableException("Everything did not take the search.");
            }

            byte[] list;
            try
            {
                list = await reply.Task.WaitAsync(ReplyBudget, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new EverythingUnavailableException("Everything did not answer in time.");
            }

            return EverythingIpc.ParseList(list, maxResults);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ready.Task.Wait(TimeSpan.FromSeconds(1)) && _ready.Task.IsCompletedSuccessfully)
        {
            PostMessage(_ready.Task.Result, WmClose, 0, 0);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetCanceled();
        }
    }

    private static unsafe bool Send(nint server, nint window, byte[] query)
    {
        fixed (byte* bytes = query)
        {
            var data = new CopyDataStruct { Data = EverythingIpc.CopyDataQueryW, Length = (uint)query.Length, Pointer = bytes };
            var sent = SendMessageTimeout(server, WmCopyData, (nuint)window, (nint)(&data), SmtoBlock | SmtoAbortIfHung, SendTimeoutMs, out var accepted);
            return sent != 0 && accepted != 0;
        }
    }

    private unsafe void Run()
    {
        var instance = GetModuleHandle(0);
        var registered = false;
        try
        {
            fixed (char* name = _ownClass)
            {
                var windowClass = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx),
                    WndProc = Marshal.GetFunctionPointerForDelegate(_procedure),
                    Instance = instance,
                    ClassName = name,
                };

                if (RegisterClassEx(&windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            registered = true;

            // A hidden top-level window, as Everything's own SDK makes: Everything sends its
            // reply here with SendMessage, which this thread's loop dispatches.
            var window = CreateWindowEx(0, _ownClass, "Hail Everything reply", 0, 0, 0, 0, 0, 0, 0, instance, 0);
            if (window == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // No message filter: Everything, elevated or not, may always send to this window, and
            // opening it wider would let a lower-integrity process forge a list of files.
            _ready.TrySetResult(window);

            Message message;
            while (GetMessage(&message, 0, 0, 0) > 0)
            {
                TranslateMessage(&message);
                DispatchMessage(&message);
            }
        }
#pragma warning disable CA1031 // The thread's own end: the failure is handed to whoever waits for the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ready.TrySetException(ex);
        }
        finally
        {
            if (registered)
            {
                UnregisterClass(_ownClass, instance);
            }

            foreach (var waiting in _pending.Values)
            {
                waiting.TrySetCanceled();
            }
        }
    }

    /// <remarks>Called by Windows on the reply thread; nothing may escape it, or the process ends.</remarks>
    private unsafe nint Procedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmCopyData:
                    return Receive((CopyDataStruct*)lParam) ? 1 : 0;
                case WmClose:
                    DestroyWindow(window);
                    return 0;
                case WmDestroy:
                    PostQuitMessage(0);
                    return 0;
            }
        }
#pragma warning disable CA1031 // A window procedure must not throw; a reply that could not be read is dropped and its search times out.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private unsafe bool Receive(CopyDataStruct* data)
    {
        if (data is null || !_pending.TryRemove((uint)data->Data, out var waiting))
        {
            return false;
        }

        if (data->Length > MaxReplyBytes || (data->Length > 0 && data->Pointer is null))
        {
            waiting.TrySetException(new InvalidDataException("Everything's reply is not a list."));
            return true;
        }

        waiting.TrySetResult(new ReadOnlySpan<byte>(data->Pointer, (int)data->Length).ToArray());
        return true;
    }
}
