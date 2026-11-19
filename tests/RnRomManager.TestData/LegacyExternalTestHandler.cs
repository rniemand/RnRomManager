using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>
/// A test-only external system with <see cref="LegacyEmbeddedRoms"/> (S2): new releases are stored next to the ZIP,
/// while games written by <see cref="LegacyZipWriter"/> keep every ROM inside it. Registered like
/// <see cref="ExternalTestHandler"/>, writing a <see cref="DatNameValue"/> fixture DAT where ingest needs one. Configurable
/// like the DS handler (ROM Zip Config B1): tests switch it through <see cref="IRomStorageSettings.SetAsync"/>.
/// </summary>
public sealed class LegacyExternalTestHandler(IRomStorageSettings? settings = null) : ISystemHandler
{
    public const string SystemId = "legacy";
    public const string DatNameValue = "Test - Legacy External";

    public string Id => SystemId;
    public string DisplayName => "Legacy External Test";
    public string Family => SystemId;
    public IReadOnlyList<string> RomExtensions => [".nds"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 64L * 1024 * 1024;
    public string DatName => DatNameValue;
    public bool EmbedRomsInZip => settings?.Override(Id) ?? DefaultEmbedRomsInZip;
    public bool DefaultEmbedRomsInZip => false;
    public bool RomStorageConfigurable => true;
    public bool LegacyEmbeddedRoms => true;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
