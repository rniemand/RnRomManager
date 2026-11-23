using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>
/// CloneCD B5/B7: <see cref="ExternalSetTestHandler"/> under the id <c>psx</c>, for the rules that treat a lone
/// PlayStation <c>.img</c> differently (it stays a lone, dismissible file).
/// </summary>
public sealed class PsxSetTestHandler : ISystemHandler
{
    public const string SystemId = "psx";

    public string Id => SystemId;
    public string DisplayName => "PlayStation Set Test";
    public string Family => SystemId;
    public IReadOnlyList<string> RomExtensions => [".bin", ".cue", ".iso", ".nrg", ".ccd", ".img", ".sub"];
    public long MinRomSizeBytes => 1;
    public long MaxRomSizeBytes => 64L * 1024 * 1024;
    public string DatName => ExternalSetTestHandler.DatNameValue;
    public bool EmbedRomsInZip => false;
    public bool FileSets => true;
    public string DatFolder => DatKind.Redump;
    public IReadOnlyList<string> MetadatKinds => [];
    public ReadOnlyMemory<byte> DefaultBoxArtPng => ExternalTestHandler.FixtureBoxArtPng;

    public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
}
