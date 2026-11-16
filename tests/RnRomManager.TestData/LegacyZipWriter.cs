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
    public static void Write(string path, InfoJsonGame game, params byte[][] roms)
    {
        if (roms.Length != game.Roms.Count)
            throw new ArgumentException("One byte array per ROM.", nameof(roms));
        game = game with { Roms = [.. game.Roms.Select(r => r with { ExternalFileName = null, Files = null })] };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var info = zip.CreateEntry(LibraryZip.InfoJsonEntry).Open())
            info.Write(InfoJsonSerializer.Serialize(game));
        foreach (var (rom, bytes) in game.Roms.Zip(roms))
        {
            using var entry = zip.CreateEntry(LibraryZip.RomEntry(rom.FileName)).Open();
            entry.Write(bytes);
        }
    }
}
