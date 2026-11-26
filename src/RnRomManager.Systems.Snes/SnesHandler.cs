using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes;

/// <summary>
/// Super Nintendo Entertainment System (SNES spec S2). <c>.sfc</c>, <c>.smc</c>, <c>.swc</c> and <c>.fig</c> dumps; a copier header is stripped
/// before hashing (S3), so headered dumps (<c>.swc</c>/<c>.fig</c> like <c>.smc</c>) match the No-Intro DAT and are stored as <c>.sfc</c>.
/// </summary>
public sealed class SnesHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = BoxArt.Lazy(typeof(SnesHandler), "boxart.snes.png");

    public string Id => SnesHeaderReader.SnesId;
    public string DisplayName => "Super Nintendo Entertainment System";
    public string ShortName => "SNES";
    public string Family => SnesHeaderReader.SnesId;

    /// <summary><c>.sfc</c> first: renamed and resolved files are stored with it.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".sfc", ".smc", ".swc", ".fig"];

    /// <summary>The smallest <c>.sfc</c> in the DAT.</summary>
    public long MinRomSizeBytes => 8192;

    /// <summary>The largest <c>.sfc</c> in the DAT (8 MiB) plus a copier header; checked before the strip.</summary>
    public long MaxRomSizeBytes => 8L * 1024 * 1024 + SnesCopierHeader.Size;

    public string DatName => "Nintendo - Super Nintendo Entertainment System";

    /// <inheritdoc />
    public bool EmbedRomsInZip => true;

    /// <summary>
    /// SNES B1: Snes9x, whose files the operator supplies (<c>Emulator:Snes9xCorePath</c>); Play shows either way and
    /// explains a missing setup. Embedded <c>.sfc</c> and legacy <c>.smc</c> entries play as stored.
    /// </summary>
    public string BrowserCore => "snes9x";

    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>Called after <see cref="NormalizeForHashing"/>, so the bytes are headerless.</summary>
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => SnesHeaderReader.Read(rom);

    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => SnesCopierHeader.Strip(rom);
}
