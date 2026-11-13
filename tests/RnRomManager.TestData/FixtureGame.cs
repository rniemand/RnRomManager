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
}
