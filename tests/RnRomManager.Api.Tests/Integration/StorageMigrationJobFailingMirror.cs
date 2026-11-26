using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

internal sealed class StorageMigrationJobFailingMirror(ILibraryMirror inner) : ILibraryMirror
{
    public Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken) =>
        replace.Count > 0 ? throw new InvalidOperationException("db down") : inner.ApplyAsync(replace, delete, cancellationToken);
}
