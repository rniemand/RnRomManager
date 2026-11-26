namespace RnRomManager.Core.Library;

/// <summary>
/// B3: the new <c>saves.json</c> (<paramref name="Index"/>, null removes it and every save) and where each listed
/// entry comes from: <paramref name="Added"/> (new bytes, keyed by entry name, each listed in the index), else the
/// existing ZIP unless the entry is in <paramref name="Removed"/>. Unlisted <c>saves/</c> entries are dropped.
/// </summary>
public sealed record SavesChange(byte[]? Index, IReadOnlyDictionary<string, Func<Stream>> Added, IReadOnlySet<string> Removed);
