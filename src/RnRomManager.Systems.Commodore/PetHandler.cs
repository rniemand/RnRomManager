using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore;

/// <summary>
/// Commodore PET. libretro has no No-Intro DAT for it, only the TOSEC one (<c>metadat/tosec/Commodore - PET.dat</c>:
/// <c>.prg</c> 770 B–27 KiB and two 174,848 B <c>.d64</c> images). Programs and disk images have no header, so
/// <see cref="ReadHeader"/> always returns null and an unmatched file goes to Review, which keeps its extension. The other
/// types VICE opens (<c>.tap</c>, <c>.t64</c>, <c>.g64</c>, <c>.d80</c>, a 1,066,496 B <c>.d82</c>, <c>.d71</c>, <c>.d81</c>, <c>.x64</c>, <c>.p00</c>) aren't in the DAT, so they come in
/// through Review; the 8 MiB cap leaves room for long tapes.
/// </summary>
public sealed class PetHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.pet.png"));

    public string Id => "pet";
    public string DisplayName => "Commodore PET";
    public string ShortName => "PET";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".prg", ".d64", ".tap", ".t64", ".g64", ".d80", ".d82", ".d71", ".d81", ".x64", ".p00"];
    public long MinRomSizeBytes => 256;
    public long MaxRomSizeBytes => 8 * 1024 * 1024;
    public string DatName => "Commodore - PET";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public string DatFolder => DatKind.Tosec;

    /// <summary>libretro has no metadat for this DAT.</summary>
    public IReadOnlyList<string> MetadatKinds => [];

    /// <inheritdoc />
    public bool KeepsSourceExtension => true;

    /// <summary>VICE (xpet), whose files the operator supplies next to the C64 ones (<c>Emulator:ViceCorePath</c>).</summary>
    public string BrowserCore => "vice_xpet";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
