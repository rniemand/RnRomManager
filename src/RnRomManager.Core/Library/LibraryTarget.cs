using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>A library ZIP opened to be written from (spec §6.1: every write starts from the ZIP's own <c>info.json</c>).</summary>
/// <param name="Zip">Open, with a valid current <c>info.json</c>; null on failure.</param>
/// <param name="Code"><c>TargetZipUnreadable</c> or <c>InfoJsonNewerSchema</c> (§23.1) on failure.</param>
public sealed record LibraryTarget(LibraryZip? Zip, InfoJsonGame? Game, string? Code, string? Details)
{
    public const string TargetZipUnreadable = "TargetZipUnreadable";
    public const string InfoJsonNewerSchema = "InfoJsonNewerSchema";

    /// <param name="zipPath">Relative to <paramref name="romsRoot"/>, <c>/</c> separators.</param>
    public static LibraryTarget Open(string romsRoot, string zipPath, InfoJsonReader reader)
    {
        LibraryZip zip;
        try
        {
            zip = LibraryZip.Open(SafePath.Resolve(romsRoot, zipPath), reader);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(null, null, TargetZipUnreadable, $"{zipPath}: {ex.Message}");
        }

        var failure = zip.InfoJson switch
        {
            { Kind: InfoJsonReadResult.Outcome.Ok } => null,
            { Kind: InfoJsonReadResult.Outcome.NewerSchema } newer => new LibraryTarget(null, null, InfoJsonNewerSchema, $"{zipPath}: schemaVersion {newer.FileVersion}"),
            _ => new LibraryTarget(null, null, TargetZipUnreadable, $"{zipPath}: info.json {zip.InfoJson?.Rule ?? "missing"}"),
        };
        if (failure is null)
            return new(zip, zip.InfoJson!.Game, null, null);
        zip.Dispose();
        return failure;
    }
}
