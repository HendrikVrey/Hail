using System.Buffers.Binary;
using System.Text;

namespace Hail.Plugin.Everything.Tests;

/// <summary>
/// The byte layout of <c>everything_ipc.h</c>'s query and list, and the reply read as another
/// process's memory: every malformed shape refused rather than followed.
/// </summary>
public sealed class EverythingIpcTests
{
    [Fact]
    public void A_query_is_five_little_endian_words_then_the_search_and_a_terminating_zero()
    {
        var bytes = EverythingIpc.BuildQuery(replyWindow: 0x1234, replyId: 7, flags: EverythingIpc.MatchPath, maxResults: 50, "cats");

        Assert.Equal(20 + ((4 + 1) * 2), bytes.Length);
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(EverythingIpc.MatchPath, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal("cats\0", Encoding.Unicode.GetString(bytes.AsSpan(20)));
    }

    [Fact]
    public void An_overlong_search_is_cut_rather_than_sent_whole()
    {
        var bytes = EverythingIpc.BuildQuery(1, 1, 0, 10, new string('x', 5000));

        Assert.Equal(20 + ((EverythingIpc.MaxSearchLength + 1) * 2), bytes.Length);
    }

    [Fact]
    public void A_list_reads_names_folders_and_kinds()
    {
        var list = ListBuilder.Build(
            (0, "report.docx", @"C:\Users\Test\Documents"),
            (1, "Documents", @"C:\Users\Test"),
            (2, "D:", string.Empty));

        var items = EverythingIpc.ParseList(list, maxItems: 10);

        Assert.Equal(3, items.Count);
        Assert.Equal(@"C:\Users\Test\Documents\report.docx", items[0].FullPath);
        Assert.False(items[0].IsFolder);
        Assert.True(items[1].IsFolder);
        Assert.True(items[2].IsDrive);
        Assert.Equal(@"D:\", items[2].FullPath);
    }

    [Fact]
    public void No_more_items_are_read_than_were_asked_for()
    {
        var list = ListBuilder.Build((0, "a", @"C:\"), (0, "b", @"C:\"), (0, "c", @"C:\"));

        Assert.Equal(2, EverythingIpc.ParseList(list, maxItems: 2).Count);
    }

    [Fact]
    public void A_reply_shorter_than_its_header_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => EverythingIpc.ParseList(new byte[27], 10));
    }

    [Fact]
    public void A_reply_claiming_more_items_than_it_holds_is_refused()
    {
        var list = ListBuilder.Build((0, "a", @"C:\"));
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(20), 1_000_000);

        Assert.Throws<InvalidDataException>(() => EverythingIpc.ParseList(list, 10));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(27u)]
    [InlineData(29u)]
    [InlineData(uint.MaxValue)]
    public void A_string_offset_outside_the_list_or_odd_is_refused(uint offset)
    {
        var list = ListBuilder.Build((0, "a", @"C:\"));
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(28 + 4), offset);

        Assert.Throws<InvalidDataException>(() => EverythingIpc.ParseList(list, 10));
    }

    [Fact]
    public void A_string_that_never_ends_is_refused()
    {
        var list = ListBuilder.Build((0, "abc", @"C:\"));
        var trimmed = list[..^2]; // the path's terminating zero, the last thing in the list

        Assert.Throws<InvalidDataException>(() => EverythingIpc.ParseList(trimmed, 10));
    }
}

/// <summary>
/// Writes an <c>EVERYTHING_IPC_LISTW</c> from the header's description, independently of the
/// parser: seven words, the items, then each name and path, zero-terminated.
/// </summary>
internal static class ListBuilder
{
    public static byte[] Build(params (uint Flags, string Name, string Path)[] items)
    {
        const int header = 28;
        const int item = 12;
        var strings = new MemoryStream();
        var offsets = new List<(uint Name, uint Path)>();
        var start = header + (items.Length * item);
        foreach (var (_, name, path) in items)
        {
            var nameAt = (uint)(start + strings.Length);
            strings.Write(Encoding.Unicode.GetBytes(name + "\0"));
            var pathAt = (uint)(start + strings.Length);
            strings.Write(Encoding.Unicode.GetBytes(path + "\0"));
            offsets.Add((nameAt, pathAt));
        }

        var bytes = new byte[start + strings.Length];
        var span = bytes.AsSpan();
        var folders = (uint)items.Count(i => (i.Flags & 1) != 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span, folders);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)items.Length - folders);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)items.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], folders);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)items.Length - folders);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)items.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0);
        for (var i = 0; i < items.Length; i++)
        {
            var at = span[(header + (i * item))..];
            BinaryPrimitives.WriteUInt32LittleEndian(at, items[i].Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(at[4..], offsets[i].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(at[8..], offsets[i].Path);
        }

        strings.ToArray().CopyTo(span[start..]);
        return bytes;
    }
}
