using System.Security.Cryptography;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;

namespace RnRomManager.TestData;

/// <summary>Builds valid <c>info.json</c> games and real library ZIPs for tests (synthetic ROMs only).</summary>
public static class TestLibrary
{
    public static readonly DateTimeOffset When = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public static byte[] RomBytes(int seed) => new SyntheticRomBuilder { Seed = seed }.Build();

    public static InfoJsonRom Rom(string fileName, byte[] bytes, bool isPrimary = true, IReadOnlyList<string>? regions = null) => new()
    {
        FileName = fileName,
        OriginalFileName = fileName,
        BaseTitle = Path.GetFileNameWithoutExtension(fileName),
        Regions = regions ?? ["World"],
        Languages = [],
        Revision = null,
        Flags = [],
        IsPrimary = isPrimary,
        Identification = new InfoJsonIdentification(IdentificationMethod.Manual),
        Header = null,
        Metadata = new InfoJsonRomMetadata(
            bytes.Length,
            Crc32.ToHex(bytes),
            Convert.ToHexStringLower(MD5.HashData(bytes)),
            Convert.ToHexStringLower(SHA1.HashData(bytes)),
            When, When, "/app/ingest/gb/" + fileName),
    };

    public static InfoJsonGame Game(string title, params InfoJsonRom[] roms) => new()
    {
        SchemaVersion = InfoJsonReader.CurrentVersion,
        GameId = Guid.NewGuid(),
        System = "gb",
        Title = title,
        DisplayTitle = title,
        ReleaseYear = null,
        Developers = [],
        Publishers = [],
        Genres = [],
        Description = null,
        Roms = roms,
        Images = [],
        Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
    };

    /// <summary>Writes <paramref name="game"/> as a ZIP at <paramref name="path"/>; ROM content is regenerated from <paramref name="seeds"/>.</summary>
    public static async Task WriteZipAsync(InfoJsonReader reader, string path, InfoJsonGame game, params int[] seeds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = game.Roms.Zip(seeds).ToDictionary(
            p => LibraryZip.RomEntry(p.First.FileName),
            p => (Func<Stream>)(() => new MemoryStream(RomBytes(p.Second))));
        await new LibraryZipWriter(reader).WriteAsync(path, null, new LibraryZipChanges(game, content));
    }
}
