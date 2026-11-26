namespace RnRomManager.Core.Library;

/// <summary>What a ZIP's <c>saves/</c> folder holds (B3).</summary>
public enum SavesState
{
    /// <summary>No <c>saves/</c> entry at all.</summary>
    Absent,

    /// <summary>A valid <c>saves.json</c> (B2).</summary>
    Valid,

    /// <summary><c>saves.json</c> missing while other <c>saves/</c> entries exist, unreadable, over 1 MiB or invalid.</summary>
    Unreadable,
}
