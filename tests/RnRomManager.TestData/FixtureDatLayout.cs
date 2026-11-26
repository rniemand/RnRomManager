using RnRomManager.Core.Dats;

namespace RnRomManager.TestData;

/// <summary>B1/B2: where a fixture system's main DAT goes and which metadat files it gets.</summary>
public sealed record FixtureDatLayout(string DatFolder, IReadOnlyList<string> MetadatKinds)
{
    public static FixtureDatLayout NoIntro { get; } = new(DatKind.NoIntro, DatKind.Metadat);

    /// <summary>The seven disc systems' layout: <c>redump</c>, no metadat.</summary>
    public static FixtureDatLayout Redump { get; } = new(DatKind.Redump, []);

    /// <summary>Commodore PET: <c>tosec</c>, no metadat.</summary>
    public static FixtureDatLayout Tosec { get; } = new(DatKind.Tosec, []);
}
