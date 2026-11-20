namespace Boatdrill.Core;

/// <summary>
/// Files whose bytes are somewhere else: an online-only file of OneDrive, Dropbox or another cloud-sync app, or one
/// that an archiving system moved offline. Reading one makes Windows download or recall it, so Boatdrill never opens one.
/// <para>
/// Windows hides from most programs that such a file is a reparse point (it "disguises" placeholders, and a .NET
/// program gets that mode), so skipping reparse points does not keep them out. These attributes tell them apart.
/// </para>
/// </summary>
internal static class CloudFiles
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: the item is virtual; opening it, or listing a folder of them, fetches it.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x40000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: reading the file downloads it. OneDrive's online-only files carry it.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>Any of the attributes that mean the bytes are not on this disk.</summary>
    public const FileAttributes NotHere = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    public static bool IsNotHere(FileAttributes attributes) => (attributes & NotHere) != 0;
}
