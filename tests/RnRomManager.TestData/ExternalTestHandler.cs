using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>
/// A test-only system that stores its ROMs outside the game ZIP (<see cref="EmbedRomsInZip"/> false), for Core,
/// Data and Api tests of external storage. It has no header and takes the default DAT source. Core and Data tests
/// put it in their own <see cref="SystemRegistry"/>; an Api test registers it through the test host's services and
/// writes a <see cref="DatNameValue"/> fixture DAT, or ingest preflight reports <c>DatsMissing</c>.
/// </summary>
public sealed class ExternalTestHandler : ISystemHandler
{
    public const string SystemId = "ext";
    public const string DatNameValue = "Test - External";

    private static readonly Lazy<ReadOnlyMemory<byte>> BoxArt = new(LoadBoxArt);

    /// <summary>The 512×512 PNG fixture every external test handler uses as its default box art.</summary>
    public static ReadOnlyMemory<byte> FixtureBoxArtPng => BoxArt.Value;

    public string Id => SystemId;
    public string DisplayName => "External Test";
    public string Family => SystemId;
    public IReadOnlyList<string> RomExtensions => [".nsp", ".xci"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 64L * 1024 * 1024;
    public string DatName => DatNameValue;
    public bool EmbedRomsInZip => false;

    /// <summary>A real PNG: <c>MediaService.Default</c> needs one even when outbound media is disabled.</summary>
    public ReadOnlyMemory<byte> DefaultBoxArtPng => BoxArt.Value;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;

    private static ReadOnlyMemory<byte> LoadBoxArt()
    {
        const string name = "RnRomManager.TestData.Resources.external-boxart.png";
        using var stream = typeof(ExternalTestHandler).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
