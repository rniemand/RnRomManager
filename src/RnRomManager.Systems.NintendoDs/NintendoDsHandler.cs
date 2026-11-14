using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.NintendoDs;

/// <summary>
/// Nintendo DS cartridge dumps (<c>.nds</c>), stored in the game's ZIP. Sizes cover the libretro No-Intro
/// <c>Nintendo - Nintendo DS</c> DAT (128 KiB–512 MiB), with the minimum left lower for homebrew. An unmatched ROM
/// with a bad header CRC is <c>InvalidHeader</c>; one with a valid CRC goes to Review.
/// </summary>
public sealed class NintendoDsHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.nds.png"));

    public string Id => NintendoDsHeaderReader.NintendoDsId;
    public string DisplayName => "Nintendo DS";
    public string ShortName => "DS";
    public string Family => Id;
    public IReadOnlyList<string> RomExtensions { get; } = [".nds"];
    public long MinRomSizeBytes => 512;

    /// <summary>512 MiB, inclusive: the largest DS cartridge in the DAT.</summary>
    public long MaxRomSizeBytes => 512L * 1024 * 1024;

    public string DatName => "Nintendo - Nintendo DS";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => NintendoDsHeaderReader.Read(rom);
}
