using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega;

/// <summary>
/// Sega 32X (32X spec X1, X2). The id is <c>s32x</c> because a CSS class can't start with a digit. 32X ROMs carry the
/// Mega Drive header (system type e.g. <c>SEGA 32X</c>), so the reader is shared; the header is optional.
/// </summary>
public sealed class Sega32xHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.sega32x.png"));

    public string Id => "s32x";
    public string DisplayName => "32X";
    public string ShortName => "32X";
    public string Family => "sega32x";
    public IReadOnlyList<string> RomExtensions { get; } = [".32x"];
    public long MinRomSizeBytes => 16384;
    public long MaxRomSizeBytes => 4 * 1024 * 1024;
    public string DatName => "Sega - 32X";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => MegaDriveHeader.Read(rom, Id);
}
