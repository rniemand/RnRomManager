namespace RnRomManager.Core.Library;

/// <summary>
/// The only writer of <c>games</c>/<c>roms</c>/<c>images</c> (spec §14.2 sync rule: ZIP first, then one transaction).
/// A replace deletes the game's rows and any row holding its <c>zip_path</c>, then inserts fresh ones from
/// <c>info.json</c>; the ZIP at a path is the truth.
/// </summary>
public interface ILibraryMirror
{
    /// <summary>All of it in one transaction. A duplicate <c>roms.sha1</c> throws and rolls back everything.</summary>
    Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken);

    Task ReplaceAsync(MirrorEntry entry, CancellationToken cancellationToken) => ApplyAsync([entry], [], cancellationToken);

    Task DeleteAsync(Guid gameId, CancellationToken cancellationToken) => ApplyAsync([], [gameId], cancellationToken);
}
