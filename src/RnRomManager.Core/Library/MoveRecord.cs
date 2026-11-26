using System.Text.Json.Serialization;

namespace RnRomManager.Core.Library;

/// <summary>
/// W3: what a multi-release move is about to do, written beside its verified source backup before any byte moves, so
/// an Admin can tell the move's two ZIPs and its release apart even after both ZIPs were replaced (W7). It binds the
/// backup (by SHA256) to the games, their library-relative ZIP paths, the moved release's SHA1 and its pointer before
/// and after the move. Neither the record nor the backup alone authorizes recovery.
/// </summary>
/// <param name="Files">
/// B18 (v2): the release's set files in <c>files</c> order, each with its name before and after the move; null in v1,
/// written by a release without files.
/// </param>
public sealed record MoveRecord(
    int Version,
    string BackupSha256,
    Guid SourceGameId,
    string SourceZipPath,
    Guid TargetGameId,
    string TargetZipPath,
    string RomSha1,
    string SourceExternalFileName,
    string TargetExternalFileName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MoveRecordFile>? Files = null)
{
    /// <summary>v1: one file per release (no <see cref="Files"/>).</summary>
    public const int SingleFileVersion = 1;

    /// <summary>v2 (B18): a release with set files.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The version a move of a release with (or without) set files writes.</summary>
    public static int VersionFor(IReadOnlyList<MoveRecordFile>? files) => files is null ? SingleFileVersion : CurrentVersion;
}
