using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Hail.Windows.Interop;

// The shell's COM surface, through the runtime's built-in COM interop. These are the
// declarations LibraryImport cannot generate (an interface returned through void**), so they
// stay DllImport and [ComImport], and every object from them is used on the STA worker only.

internal enum SIGDN : uint
{
    NormalDisplay = 0x00000000,
    ParentRelativeParsing = 0x80018001,
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [return: MarshalAs(UnmanagedType.Interface)]
    object BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId);

    IShellItem GetParent();

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetDisplayName(SIGDN form);

    uint GetAttributes(uint mask);

    int Compare(IShellItem other, uint hint);
}

[ComImport]
[Guid("70629033-e363-4a28-a567-0db78006e6d7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    /// <summary>S_OK with one item, S_FALSE at the end.</summary>
    [PreserveSig]
    int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);

    void Skip(uint count);

    void Reset();

    IEnumShellItems Clone();
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(SIZE size, uint flags, out nint bitmap);
}

[SuppressMessage(
    "Interoperability",
    "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time",
    Justification = "These return COM interfaces through void**, which LibraryImport marshals only for [GeneratedComInterface] types; the shell interfaces here are [ComImport].")]
internal static class ShellNative
{
    public static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    public static readonly Guid HandlerEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    public static readonly Guid IidShellItem = typeof(IShellItem).GUID;
    public static readonly Guid IidEnumShellItems = typeof(IEnumShellItems).GUID;
    public static readonly Guid IidShellItemImageFactory = typeof(IShellItemImageFactory).GUID;

    public const uint SIIGBF_ICONONLY = 0x04;

    [DllImport("shell32.dll", ExactSpelling = true, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern object SHGetKnownFolderItem(in Guid folderId, uint flags, nint token, in Guid interfaceId);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern object SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId);
}
