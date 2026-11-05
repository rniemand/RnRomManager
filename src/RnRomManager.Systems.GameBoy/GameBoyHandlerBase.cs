using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.GameBoy;

/// <summary>Shared by the GB and GBC handlers: one family, one header format, same limits (spec §8, §8.4).</summary>
public abstract class GameBoyHandlerBase : ISystemHandler
{
    public const string FamilyId = "gameboy";

    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt;

    protected GameBoyHandlerBase() => _boxArt = new(() => EmbeddedResources.Load($"boxart.{Id}.png"));

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public string Family => FamilyId;
    public abstract IReadOnlyList<string> RomExtensions { get; }

    /// <summary>The header ends at 0x0150; anything shorter can't be a cart.</summary>
    public long MinRomSizeBytes => GameBoyHeaderReader.HeaderEnd;

    /// <summary>16 MiB: the DATs hold 8 KiB–16 MiB ROMs, including aftermarket carts.</summary>
    public long MaxRomSizeBytes => 16L * 1024 * 1024;

    public abstract string DatName { get; }
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => GameBoyHeaderReader.Read(rom);
}
