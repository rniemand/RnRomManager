using RnRomManager.Core.Dats;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDs;

/// <summary>
/// Nintendo 3DS dumps (<c>.3ds</c>, <c>.cci</c>, <c>.cia</c>), stored next to the game's ZIP with the source extension.
/// Sizes cover the libretro No-Intro <c>Nintendo - Nintendo 3DS</c> DAT (126 MiB–4 GiB), with the minimum left lower for
/// homebrew. No browser play: EmulatorJS 4.2.3 ships no 3DS core. An Admin may store them inside the ZIP instead
/// (ROM Zip Config, <see cref="RomStorageConfigurable"/>); imports then read each ROM into memory, so dumps over
/// <see cref="Array.MaxLength"/> bytes (about 2 GiB) are refused as too large until the setting goes back.
/// </summary>
public sealed class Nintendo3DsHandler(IRomStorageSettings? settings = null) : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.3ds.png"));

    public string Id => Nintendo3DsHeaderReader.Nintendo3DsId;
    public string DisplayName => "Nintendo 3DS";
    public string ShortName => "3DS";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".3ds", ".cci", ".cia"];
    public long MinRomSizeBytes => 16 * 1024;

    /// <summary>4 GiB, inclusive: the largest entry in the DAT.</summary>
    public long MaxRomSizeBytes => 4L * 1024 * 1024 * 1024;

    public string DatName => "Nintendo - Nintendo 3DS";

    /// <summary>libretro has no 3DS <c>releaseyear</c> metadat.</summary>
    public IReadOnlyList<string> MetadatKinds { get; } = [DatKind.Developer, DatKind.Publisher, DatKind.Genre];

    /// <summary>B3: the Admin's setting; beside the ZIP by default (and with no settings, in tests).</summary>
    public bool EmbedRomsInZip => settings?.Override(Id) ?? DefaultEmbedRomsInZip;

    /// <inheritdoc />
    public bool DefaultEmbedRomsInZip => false;

    /// <inheritdoc />
    public bool RomStorageConfigurable => true;

    /// <inheritdoc />
    public bool LegacyEmbeddedRoms => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => Nintendo3DsHeaderReader.Read(rom);
}
