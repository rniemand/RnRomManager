using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore VIC-20. Matched against libretro's No-Intro <c>Commodore - VIC-20</c> DAT: raw cartridge dumps of 4–16 KiB
/// whose extension names the load address (<c>.20</c>, <c>.40</c>, <c>.60</c>, <c>.a0</c>; one <c>.bin</c>), so Review keeps
/// the source extension, which VICE needs to map the cartridge. The dumps have no header, so <see cref="ReadHeader"/>
/// always returns null and an unmatched file goes to Review.
/// </summary>
public sealed class Vic20Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.vic20.png"));

    public string Id => "vic20";
    public string DisplayName => "Commodore VIC-20";
    public string ShortName => "VIC-20";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".a0", ".20", ".40", ".60", ".bin"];
    public long MinRomSizeBytes => 2 * 1024;
    public long MaxRomSizeBytes => 64 * 1024;
    public string DatName => "Commodore - VIC-20";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>VICE (xvic), whose files the operator supplies next to the C64 ones (<c>Emulator:ViceCorePath</c>).</summary>
    public string BrowserCore => "vice_xvic";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
