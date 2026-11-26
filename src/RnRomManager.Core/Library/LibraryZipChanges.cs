using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>
/// What a write puts in the ZIP: the complete new <c>info.json</c>, plus content for the entries that are new or
/// replaced (full entry names, e.g. <c>roms/Tetris (World).gb</c>). Every other entry the new <c>info.json</c> lists
/// is copied from the existing ZIP; entries it no longer lists are dropped. The note of every listed ROM
/// (<see cref="LibraryZip.NoteEntry"/>) is copied too unless it's in <paramref name="NewEntries"/> or
/// <paramref name="RemovedEntries"/>; notes of ROMs no longer listed are dropped.
/// <para>
/// A ROM with an <c>externalFileName</c> has no entry: its bytes are a file next to the ZIP, so a
/// <c>roms/</c> entry for it in <paramref name="NewEntries"/> is rejected like any unlisted entry.
/// </para>
/// </summary>
/// <param name="ExternalFiles">
/// The external ROM files this write placed or renamed, keyed by <c>externalFileName</c> (compared ignoring case),
/// each with the snapshot its move returned. A checked write verifies exactly these, before and at publication;
/// external files the write didn't touch are not looked at, so a game with a missing file can still be edited.
/// </param>
/// <param name="Saves">B3: null carries the existing <c>saves/</c> folder; set only by the save operations and merge.</param>
public sealed record LibraryZipChanges(
    InfoJsonGame Info,
    IReadOnlyDictionary<string, Func<Stream>> NewEntries,
    IReadOnlySet<string>? RemovedEntries = null,
    IReadOnlyDictionary<string, ExternalFileSnapshot>? ExternalFiles = null,
    SavesChange? Saves = null);
