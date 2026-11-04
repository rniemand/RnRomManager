namespace RnRomManager.Core.Dats;

/// <summary>Merged metadat for one CRC (spec §10.1).</summary>
public sealed record DatMetadata(int? ReleaseYear, IReadOnlyList<string> Developers, IReadOnlyList<string> Publishers, IReadOnlyList<string> Genres)
{
    public static DatMetadata Empty { get; } = new(null, [], [], []);
}
