using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>
/// A test-only external system identified through titledb (spec T1–T5), used before the real Switch handler is
/// registered. Its header is plausible unless the ROM starts with <c>0xFF</c>, and carries <c>titleId</c> only when one
/// is given, so tests can reach file-name matching and both unmatched branches.
/// </summary>
public sealed class ExternalTitleDbTestHandler(string? headerTitleId = null) : ISystemHandler
{
    public const string SystemId = "extdb";
    public const string DatNameValue = "Test - External TitleDb";

    public string Id => SystemId;
    public string DisplayName => "External TitleDb Test";
    public string Family => SystemId;
    public IReadOnlyList<string> RomExtensions => [".nsp", ".xci"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 64L * 1024 * 1024;
    public string DatName => DatNameValue;
    public bool EmbedRomsInZip => false;
    public DatSource DatSource => DatSource.TitleDb;
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => new(SystemId, null, IsPlausible: rom.Length == 0 || rom[0] != 0xFF,
        headerTitleId is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["titleId"] = headerTitleId });
}
