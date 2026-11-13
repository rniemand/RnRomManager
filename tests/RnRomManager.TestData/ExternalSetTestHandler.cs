using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>
/// A test-only disc system with file sets (B5): external storage, exact names, bin/cue sets, and the <c>redump</c>
/// layout with no metadat like the six real disc handlers. Registered only by the test hosts that use it, each
/// writing a <see cref="DatNameValue"/> fixture DAT with <see cref="FixtureDatLayout.Redump"/>.
/// </summary>
public sealed class ExternalSetTestHandler : ISystemHandler
{
    public const string SystemId = "extset";
    public const string DatNameValue = "Test - External Set";

    public string Id => SystemId;
    public string DisplayName => "External Set Test";
    public string Family => SystemId;
    public IReadOnlyList<string> RomExtensions => [".bin", ".cue", ".iso", ".nrg"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 64L * 1024 * 1024;
    public string DatName => DatNameValue;
    public bool EmbedRomsInZip => false;
    public bool FileSets => true;
    public string DatFolder => DatKind.Redump;
    public IReadOnlyList<string> MetadatKinds => [];
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
