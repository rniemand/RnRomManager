using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.GameBoy;

/// <summary>Game Boy Advance (GBA spec B2). Its own family: no GB/GBC header can classify a GBA file, or the reverse.</summary>
public sealed class GameBoyAdvanceHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => EmbeddedResources.Load("boxart.gba.png"));

    public string Id => GameBoyAdvanceHeaderReader.GameBoyAdvanceId;
    public string DisplayName => "Game Boy Advance";
    public string Family => GameBoyAdvanceHeaderReader.GameBoyAdvanceId;
    public IReadOnlyList<string> RomExtensions { get; } = [".gba"];

    /// <summary>The header ends at 0xC0; anything shorter can't be a cart.</summary>
    public long MinRomSizeBytes => GameBoyAdvanceHeaderReader.HeaderEnd;

    /// <summary>32 MiB: the largest GBA cart.</summary>
    public long MaxRomSizeBytes => 32L * 1024 * 1024;

    public string DatName => "Nintendo - Game Boy Advance";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => GameBoyAdvanceHeaderReader.Read(rom);
}
