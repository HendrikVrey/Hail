using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Hail.Windows;

/// <summary>What a second start can ask of the running Hail.</summary>
public enum InstanceCommand
{
    /// <summary>Show the box: what opening Hail from the Start menu does.</summary>
    Show,

    /// <summary>Quit: <c>Hail.exe --quit</c>, for scripts and the installer.</summary>
    Quit,
}

/// <summary>
/// One Hail per user session (Hail.md §5). A second start does not become a twin: it asks the
/// first to show the box, then exits, so opening Hail from the Start menu summons it.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a named mutex in the session's own namespace. The request is a named pipe,
/// which is machine-wide, so its name carries the user's SID and the session id, and it is
/// opened with <see cref="PipeOptions.CurrentUserOnly"/>: no other account can connect to it,
/// and no other account can create it first to intercept the request.
/// </para>
/// <para>
/// The pipe accepts one of the <see cref="InstanceCommand"/> words and nothing else. Anything
/// else, or anything longer, is ignored. Quit needs no more protection than that: only the
/// same user can reach the pipe, and the same user can end the process anyway.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 16;

    private readonly Mutex _mutex;
    private readonly string _pipeName;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Claims the session for this process; null if another Hail already holds it.</summary>
    public static SingleInstance? TryClaim(string name = "Hail")
    {
        var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}.Instance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, PipeName(name));
    }

    /// <summary>Sends the running Hail a command. False when it could not be reached.</summary>
    public static async Task<bool> SignalAsync(InstanceCommand command, string name = "Hail", TimeSpan? timeout = null)
    {
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            await client.ConnectAsync(cancel.Token).ConfigureAwait(false);
            await client.WriteAsync(Encoding.ASCII.GetBytes(Word(command)), cancel.Token).ConfigureAwait(false);
            await client.FlushAsync(cancel.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Answers requests until <paramref name="ct"/> is cancelled, calling
    /// <paramref name="onCommand"/> for each command understood. A broken or unexpected
    /// connection is dropped and the next one awaited.
    /// </summary>
    public async Task ListenAsync(Action<InstanceCommand> onCommand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onCommand);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var message = await ReadMessageAsync(server, ct).ConfigureAwait(false);
                foreach (var command in Enum.GetValues<InstanceCommand>())
                {
                    if (string.Equals(message, Word(command), StringComparison.Ordinal))
                    {
                        onCommand(command);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A client that vanished mid-message; wait for the next one.
            }
        }
    }

    /// <summary>
    /// Gives the claim up. The mutex is never owned, only held open: its existence is the claim,
    /// so closing it from any thread is enough and nothing is left abandoned.
    /// </summary>
    public void Dispose()
    {
        _mutex.Dispose();
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));

        var buffer = new byte[MaxMessageBytes + 1];
        var read = 0;
        try
        {
            int chunk;
            while (read < buffer.Length && (chunk = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false)) > 0)
            {
                read += chunk;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        return read is > 0 and <= MaxMessageBytes ? Encoding.ASCII.GetString(buffer, 0, read) : null;
    }

    private static string Word(InstanceCommand command) => command switch
    {
        InstanceCommand.Show => "show",
        InstanceCommand.Quit => "quit",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static string PipeName(string name)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        using var process = Process.GetCurrentProcess();
        return $"{name}.{user}.{process.SessionId}";
    }
}
