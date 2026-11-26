namespace RnRomManager.TestData;

/// <summary>One game for <see cref="FixtureDatWriter"/>: its DAT names, the ROM bytes (hashed for real) and optional metadat.</summary>
public sealed record FixtureGame(string GameName, string RomName, byte[] Rom)
{
    public int? ReleaseYear { get; init; }

    public string? Serial { get; init; }

    /// <summary>Each value is written as its own metadat block, so a CRC with two developers has two blocks (spec §10.1).</summary>
    public IReadOnlyList<string> Developers { get; init; } = [];

    public IReadOnlyList<string> Publishers { get; init; } = [];

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Precomputed hashes, used instead of hashing <see cref="Rom"/> (see <see cref="FromFile"/>).</summary>
    public FixtureRomHashes? Hashes { get; init; }

    /// <summary>What the DAT records: <see cref="Hashes"/>, or the hashes of <see cref="Rom"/>.</summary>
    public FixtureRomHashes RomHashes => Hashes ?? FixtureRomHashes.Of(Rom);

    /// <summary>
    /// A game for a ROM file on disk, hashed in one streamed read so a 512 MiB file is never held in memory. The game name is
    /// the file name without its extension, the ROM name the file name.
    /// </summary>
    public static FixtureGame FromFile(string path)
    {
        var name = Path.GetFileName(path);
        return new FixtureGame(Path.GetFileNameWithoutExtension(name), name, []) { Hashes = FixtureRomHashes.OfFile(path) };
    }
}
