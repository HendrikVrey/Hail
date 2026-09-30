using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using static Hail.Plugin.Everything.NativeMethods;

namespace Hail.Plugin.Everything.Tests;

/// <summary>
/// The client's reply window against a fake Everything: a window of another class that reads
/// the query from WM_COPYDATA and sends a list back the way <c>everything_ipc.h</c> describes.
/// It proves the client's window, thread and message plumbing; it cannot prove that the real
/// Everything agrees with the header, which is Hendrik's check (Hail.md §19).
/// </summary>
public sealed class EverythingClientTests
{
    [Fact]
    public async Task A_search_goes_out_as_a_query_and_the_reply_comes_back_as_items()
    {
        using var server = new FakeEverythingWindow();
        using var client = new EverythingClient(server.ClassName);

        var items = await client.SearchAsync("report", EverythingIpc.MatchPath, 25, CancellationToken.None);

        Assert.Equal(("report", EverythingIpc.MatchPath, 25u), server.Received.Single());
        var item = Assert.Single(items);
        Assert.Equal(@"C:\Found\report.txt", item.FullPath);
    }

    [Fact]
    public async Task Searches_at_once_each_get_their_own_reply()
    {
        using var server = new FakeEverythingWindow();
        using var client = new EverythingClient(server.ClassName);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.SearchAsync($"q{i}", 0, 10, CancellationToken.None)));

        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"q{i}.txt"), answers.Select(a => a.Single().Name));
    }

    [Fact]
    public async Task Without_everything_the_search_says_it_is_not_running()
    {
        using var client = new EverythingClient($"Hail.Tests.NoSuchWindow.{Guid.NewGuid():N}");

        var refused = await Assert.ThrowsAsync<EverythingUnavailableException>(() => client.SearchAsync("x", 0, 10, CancellationToken.None));

        Assert.Equal("Everything is not running.", refused.Message);
    }

    [Fact]
    public async Task A_disposed_client_refuses_to_search()
    {
        var client = new EverythingClient($"Hail.Tests.NoSuchWindow.{Guid.NewGuid():N}");
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SearchAsync("x", 0, 10, CancellationToken.None));
    }

    /// <summary>A window on its own thread that answers WM_COPYDATA queries as Everything does.</summary>
    private sealed unsafe class FakeEverythingWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<nint> _ready = new();
        private readonly WindowProcedure _procedure;
        private readonly List<(string Search, uint Flags, uint Max)> _received = [];

        public FakeEverythingWindow()
        {
            _procedure = Procedure;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            _ready.Task.Wait(TimeSpan.FromSeconds(5));
        }

        private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

        public string ClassName { get; } = $"Hail.Tests.FakeEverything.{Guid.NewGuid():N}";

        public IReadOnlyList<(string Search, uint Flags, uint Max)> Received
        {
            get
            {
                lock (_received)
                {
                    return [.. _received];
                }
            }
        }

        public void Dispose()
        {
            PostMessage(_ready.Task.Result, WmClose, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void Run()
        {
            var instance = GetModuleHandle(0);
            fixed (char* name = ClassName)
            {
                var windowClass = new WndClassEx { Size = (uint)sizeof(WndClassEx), WndProc = Marshal.GetFunctionPointerForDelegate(_procedure), Instance = instance, ClassName = name };
                RegisterClassEx(&windowClass);
            }

            _ready.SetResult(CreateWindowEx(0, ClassName, "fake", 0, 0, 0, 0, 0, 0, 0, instance, 0));
            Message message;
            while (GetMessage(&message, 0, 0, 0) > 0)
            {
                DispatchMessage(&message);
            }

            UnregisterClass(ClassName, instance);
        }

        private nint Procedure(nint window, uint message, nuint wParam, nint lParam)
        {
            switch (message)
            {
                case WmCopyData:
                    var data = (CopyDataStruct*)lParam;
                    if (data->Data != EverythingIpc.CopyDataQueryW)
                    {
                        return 0;
                    }

                    var query = new ReadOnlySpan<byte>(data->Pointer, (int)data->Length).ToArray();
                    var replyWindow = (nint)BinaryPrimitives.ReadUInt32LittleEndian(query);
                    var replyId = BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(4));
                    var search = Encoding.Unicode.GetString(query.AsSpan(20)).TrimEnd('\0');
                    lock (_received)
                    {
                        _received.Add((search, BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(16))));
                    }

                    // Everything answers later, from its own queue; so does this.
                    ThreadPool.QueueUserWorkItem(_ => Reply(replyWindow, replyId, ListBuilder.Build((0, search + ".txt", @"C:\Found"))));
                    return 1;
                case WmClose:
                    DestroyWindow(window);
                    return 0;
                case WmDestroy:
                    PostQuitMessage(0);
                    return 0;
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private static void Reply(nint window, uint id, byte[] list)
        {
            fixed (byte* bytes = list)
            {
                var data = new CopyDataStruct { Data = id, Length = (uint)list.Length, Pointer = bytes };
                SendMessageTimeout(window, WmCopyData, 0, (nint)(&data), SmtoBlock, 2000, out _);
            }
        }
    }
}
