using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>The real mirror, made to fail on demand (a DB update that fails after the ZIP was written).</summary>
internal sealed class AuditLibraryEditFailingMirror(ILibraryMirror inner) : ILibraryMirror
{
    public bool Fail { get; set; }

    public Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken) =>
        Fail ? throw new TimeoutException("DB down") : inner.ApplyAsync(replace, delete, cancellationToken);
}
