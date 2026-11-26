using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDs;

/// <summary>
/// Nintendo DS cartridge dumps (<c>.nds</c>, and iQue DS <c>.ids</c>), stored next to the game's ZIP (S1); games from older versions keep them
/// inside it until the storage migration job moves them out (<see cref="LegacyEmbeddedRoms"/>). Sizes cover the libretro No-Intro
/// <c>Nintendo - Nintendo DS</c> DAT (128 KiB–512 MiB), with the minimum left lower for homebrew. An unmatched ROM
/// with a bad header CRC is <c>InvalidHeader</c>; one with a valid CRC goes to Review. An Admin may store them inside the
/// ZIP instead (ROM Zip Config, <see cref="RomStorageConfigurable"/>).
/// </summary>
public sealed class NintendoDsHandler(IRomStorageSettings? settings = null) : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(NintendoDsHandler), "boxart.nds.png");

    public string Id => NintendoDsHeaderReader.NintendoDsId;
    public string DisplayName => "Nintendo DS";
    public string ShortName => "DS";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".nds", ".ids"];
    public long MinRomSizeBytes => 512;

    /// <summary>512 MiB, inclusive: the largest DS cartridge in the DAT.</summary>
    public long MaxRomSizeBytes => 512L * 1024 * 1024;

    public string DatName => "Nintendo - Nintendo DS";

    /// <summary>
    /// DeSmuME from EmulatorJS 4.2.3, vendored (non-threaded payloads only). The browser size gate on 2026-11-14 passed
    /// at 512 MiB, so Play has no size limit below the import maximum.
    /// </summary>
    public string BrowserCore => "desmume";

    /// <summary>B3: the Admin's setting; beside the ZIP by default (and with no settings, in tests).</summary>
    public bool EmbedRomsInZip => settings?.Override(Id) ?? DefaultEmbedRomsInZip;

    /// <inheritdoc />
    public bool DefaultEmbedRomsInZip => false;

    /// <inheritdoc />
    public bool RomStorageConfigurable => true;

    /// <inheritdoc />
    public bool LegacyEmbeddedRoms => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => NintendoDsHeaderReader.Read(rom);
}
