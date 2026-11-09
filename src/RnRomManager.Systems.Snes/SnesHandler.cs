using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes;

/// <summary>
/// Super Nintendo Entertainment System (SNES spec S2). <c>.sfc</c> and <c>.smc</c> dumps; a copier header is stripped
/// before hashing (S3), so headered dumps match the No-Intro DAT and are stored as <c>.sfc</c>.
/// </summary>
public sealed class SnesHandler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.snes.png"));

    public string Id => SnesHeaderReader.SnesId;
    public string DisplayName => "Super Nintendo Entertainment System";
    public string Family => SnesHeaderReader.SnesId;

    /// <summary><c>.sfc</c> first: renamed and resolved files are stored with it.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".sfc", ".smc"];

    /// <summary>The smallest <c>.sfc</c> in the DAT.</summary>
    public long MinRomSizeBytes => 8192;

    /// <summary>The largest <c>.sfc</c> in the DAT (8 MiB) plus a copier header; checked before the strip.</summary>
    public long MaxRomSizeBytes => 8L * 1024 * 1024 + SnesCopierHeader.Size;

    public string DatName => "Nintendo - Super Nintendo Entertainment System";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>Called after <see cref="NormalizeForHashing"/>, so the bytes are headerless.</summary>
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => SnesHeaderReader.Read(rom);

    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) => SnesCopierHeader.Strip(rom);

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(SnesHandler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Snes.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
