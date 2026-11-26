using System.IO.Compression;
using RnRomManager.Core.Library;

namespace RnRomManager.TestData;

/// <summary>
/// Writes a legacy game ZIP (S2) for any handler: an all-embedded <c>info.json</c> plus its <c>roms/</c> entries,
/// directly, since <see cref="LibraryZipWriter"/> refuses that shape for an external system.
/// </summary>
public static class LegacyZipWriter
{
    /// <param name="roms">Each ROM's bytes, in the order of <paramref name="game"/>'s ROMs.</param>
    public static void Write(string path, InfoJsonGame game, params byte[][] roms) => Write(path, game, null, roms);

    /// <param name="extra">More entries by name, e.g. <c>images/…</c> or <c>notes/…</c>.</param>
    /// <param name="roms">Each ROM's bytes, in the order of <paramref name="game"/>'s ROMs.</param>
    public static void Write(
        string path,
        InfoJsonGame game,
        IReadOnlyDictionary<string, byte[]>? extra,
        params byte[][] roms)
    {
        if (roms.Length != game.Roms.Count)
            throw new ArgumentException("One byte array per ROM.", nameof(roms));
        game = game with { Roms = [.. game.Roms.Select(r => r with { ExternalFileName = null, Files = null })] };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var info = zip.CreateEntry(LibraryZip.InfoJsonEntry).Open())
            info.Write(InfoJsonSerializer.Serialize(game));
        foreach (var (name, bytes) in game.Roms.Select(r => LibraryZip.RomEntry(r.FileName)).Zip(roms).Concat(extra?.Select(e => (e.Key, e.Value)) ?? []))
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(bytes);
        }
    }
}
