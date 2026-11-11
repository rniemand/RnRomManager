using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

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

        return Check(zip, zipPath);
    }

    /// <summary>
    /// The open for an external game's write (spec W1, I4): the path is checked below <paramref name="romsRoot"/> and
    /// read through one checked handle, and the ZIP must be the game its mirror row names. A ZIP of another game or
    /// system is <c>TargetZipUnreadable</c> before any file next to it is touched.
    /// </summary>
    public static LibraryTarget OpenChecked(string romsRoot, string zipPath, InfoJsonReader reader, Guid gameId, string systemId)
    {
        LibraryZip zip;
        try
        {
            var opened = ExternalRomPath.OpenRead(romsRoot, SafePath.Resolve(romsRoot, zipPath), FileShare.Read | FileShare.Delete);
            if (opened.Status != ExternalPathStatus.Found)
                return new(null, null, TargetZipUnreadable, $"{zipPath}: {opened.Details}");
            zip = LibraryZip.Open(new BufferedStream(opened.Stream!), opened.Path, reader, opened.Snapshot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(null, null, TargetZipUnreadable, $"{zipPath}: {ex.Message}");
        }

        var target = Check(zip, zipPath);
        return target.Game is null || (target.Game.GameId == gameId && target.Game.System == systemId) ? target : Mismatch(target, zipPath, gameId, systemId);
    }

    /// <summary>
    /// The open for any write to a mirrored game (G4, I4): checked, and held to the mirror row's identity, when the
    /// row's system or the ZIP's top folder is external, so an embedded game mirrored under an external folder is
    /// checked too. A plainly opened ZIP whose <c>info.json</c> names an external system other than the row's is
    /// refused the same way.
    /// </summary>
    public static LibraryTarget OpenForWrite(string romsRoot, string zipPath, InfoJsonReader reader, ISystemRegistry systems, Guid gameId, string systemId)
    {
        if (NeedsCheck(systems, zipPath, systemId))
            return OpenChecked(romsRoot, zipPath, reader, gameId, systemId);
        var target = Open(romsRoot, zipPath, reader);
        return target.Game is { } game && game.System != systemId && IsExternal(systems, game.System)
            ? Mismatch(target, zipPath, gameId, systemId)
            : target;
    }

    /// <summary>G4: the ZIP's path is checked when the game's system or the ZIP's top folder is external.</summary>
    public static bool NeedsCheck(ISystemRegistry systems, string zipPath, string systemId) =>
        IsExternal(systems, systemId) || (zipPath.IndexOf('/') is var slash and > 0 && IsExternal(systems, zipPath[..slash]));

    private static bool IsExternal(ISystemRegistry systems, string systemId) =>
        systems.All.Any(h => !h.EmbedRomsInZip && string.Equals(h.Id, systemId, StringComparison.OrdinalIgnoreCase));

    private static LibraryTarget Mismatch(LibraryTarget target, string zipPath, Guid gameId, string systemId)
    {
        target.Zip!.Dispose();
        return new(null, null, TargetZipUnreadable,
            $"{zipPath}: info.json is game {target.Game!.GameId} of {target.Game.System}, not the mirrored game {gameId} of {systemId}; the mirror is stale");
    }

    private static LibraryTarget Check(LibraryZip zip, string zipPath)
    {
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
