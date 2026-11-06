using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Nintendo64;

/// <summary>
/// Nintendo 64 (N64 spec N2). Every dump is hashed and stored big-endian (<c>.z64</c>), whatever order it came in, so it
/// matches the No-Intro DAT.
/// </summary>
public sealed class Nintendo64Handler : ISystemHandler
{
    private readonly Lazy<ReadOnlyMemory<byte>> _boxArt = new(() => LoadResource("boxart.n64.png"));

    public string Id => Nintendo64HeaderReader.Nintendo64Id;
    public string DisplayName => "Nintendo 64";
    public string Family => Nintendo64HeaderReader.Nintendo64Id;

    /// <summary><c>.z64</c> first: renamed and resolved files are stored with it.</summary>
    public IReadOnlyList<string> RomExtensions { get; } = [".z64", ".v64", ".n64"];

    /// <summary>The header plus the boot code.</summary>
    public long MinRomSizeBytes => 0x1000;

    /// <summary>64 MiB: the largest N64 cart.</summary>
    public long MaxRomSizeBytes => 64L * 1024 * 1024;

    public string DatName => "Nintendo - Nintendo 64";
    public ReadOnlyMemory<byte> DefaultBoxArtPng => _boxArt.Value;

    /// <summary>Called after <see cref="NormalizeForHashing"/>, so the bytes are big-endian.</summary>
    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => Nintendo64HeaderReader.Read(rom);

    public ReadOnlyMemory<byte> NormalizeForHashing(ReadOnlyMemory<byte> rom) =>
        Nintendo64ByteOrder.ToBigEndian(rom.ToArray());

    private static byte[] LoadResource(string name)
    {
        using var stream = typeof(Nintendo64Handler).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Nintendo64.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
