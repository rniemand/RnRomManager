namespace RnRomManager.Core.Collections;

/// <summary>A PATCH (B4): a null <see cref="Name"/> is unchanged; <see cref="Notes"/> applies only when <see cref="NotesSet"/>.</summary>
public sealed record CollectionPatch(string? Name = null, bool NotesSet = false, string? Notes = null);
