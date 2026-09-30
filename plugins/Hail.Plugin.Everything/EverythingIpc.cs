using System.Buffers.Binary;
using System.Text;

namespace Hail.Plugin.Everything;

/// <summary>One file or folder Everything found.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Folder">The folder that holds it; empty for a drive.</param>
public readonly record struct EverythingItem(string Name, string Folder, bool IsFolder, bool IsDrive)
{
    /// <summary>The item's full path; a drive's is its root, <c>C:\</c>.</summary>
    public string FullPath => IsDrive || Folder.Length == 0 ? Name.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar : Path.Combine(Folder, Name);
}

/// <summary>
/// Everything's query and reply, as <c>everything_ipc.h</c> (Everything 1.4) lays them out: a
/// query goes to Everything's window as <c>WM_COPYDATA</c>, and the list comes back to ours the
/// same way. Pure, so the byte layout is tested without Everything.
/// </summary>
/// <remarks>
/// The reply is another process's memory, so it is read defensively: every offset is checked
/// against the length before it is followed, and every string must end inside the data.
/// </remarks>
internal static class EverythingIpc
{
    /// <summary>Everything 1.4's IPC window class.</summary>
    public const string WindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    /// <summary><c>EVERYTHING_IPC_COPYDATAQUERYW</c>: the <c>dwData</c> of a UTF-16 query.</summary>
    public const nuint CopyDataQueryW = 2;

    public const uint MatchCase = 0x01;
    public const uint MatchWholeWord = 0x02;
    public const uint MatchPath = 0x04;

    /// <summary>The longest search sent; more than anyone types into a launcher.</summary>
    public const int MaxSearchLength = 1024;

    private const uint ItemFolder = 0x01;
    private const uint ItemDrive = 0x02;

    /// <summary><c>EVERYTHING_IPC_QUERYW</c>: five DWORDs, then the search, then a terminating zero.</summary>
    private const int QueryHeader = 5 * sizeof(uint);

    /// <summary><c>EVERYTHING_IPC_LISTW</c>'s seven DWORDs before its items.</summary>
    private const int ListHeader = 7 * sizeof(uint);

    /// <summary><c>EVERYTHING_IPC_ITEMW</c>: flags, name offset, path offset.</summary>
    private const int ItemSize = 3 * sizeof(uint);

    /// <summary>Windows' longest path, in characters; a longer string in a reply is not a path.</summary>
    private const int MaxPathLength = 32767;

    /// <summary>The bytes of a query whose reply comes to <paramref name="replyWindow"/> tagged <paramref name="replyId"/>.</summary>
    /// <param name="replyWindow">
    /// Our window's handle. Only 32 bits: a window handle fits in them on 64-bit Windows too,
    /// which is why the structure has room for no more.
    /// </param>
    public static byte[] BuildQuery(uint replyWindow, uint replyId, uint flags, uint maxResults, string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Length > MaxSearchLength)
        {
            search = search[..MaxSearchLength];
        }

        var bytes = new byte[QueryHeader + ((search.Length + 1) * sizeof(char))];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, replyWindow);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], replyId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 0); // offset: from the first result
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], maxResults);
        Encoding.Unicode.GetBytes(search, span[QueryHeader..]);
        return bytes;
    }

    /// <summary>
    /// The items of a reply, at most <paramref name="maxItems"/>. Throws
    /// <see cref="InvalidDataException"/> when the reply is not shaped as a list.
    /// </summary>
    public static IReadOnlyList<EverythingItem> ParseList(ReadOnlySpan<byte> data, int maxItems)
    {
        if (data.Length < ListHeader)
        {
            throw new InvalidDataException("The reply is shorter than a list.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]); // numitems
        if (count > (uint)((data.Length - ListHeader) / ItemSize))
        {
            throw new InvalidDataException("The reply lists more items than it holds.");
        }

        var items = new List<EverythingItem>((int)Math.Min(count, (uint)maxItems));
        for (var i = 0; i < count && items.Count < maxItems; i++)
        {
            var item = data.Slice(ListHeader + (i * ItemSize), ItemSize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(item);
            var name = ReadString(data, BinaryPrimitives.ReadUInt32LittleEndian(item[4..]));
            var folder = ReadString(data, BinaryPrimitives.ReadUInt32LittleEndian(item[8..]));
            items.Add(new EverythingItem(name, folder, (flags & ItemFolder) != 0, (flags & ItemDrive) != 0));
        }

        return items;
    }

    /// <summary>A zero-terminated UTF-16 string at <paramref name="offset"/> bytes from the list's start.</summary>
    private static string ReadString(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset < ListHeader || offset >= data.Length || offset % sizeof(char) != 0)
        {
            throw new InvalidDataException("A string in the reply is outside it.");
        }

        var rest = data[(int)offset..];
        for (var end = 0; end + 1 < rest.Length; end += sizeof(char))
        {
            if (rest[end] == 0 && rest[end + 1] == 0)
            {
                return end / sizeof(char) <= MaxPathLength
                    ? Encoding.Unicode.GetString(rest[..end])
                    : throw new InvalidDataException("A string in the reply is longer than a path.");
            }
        }

        throw new InvalidDataException("A string in the reply does not end.");
    }
}
