using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Runs <c>before</c>, the mirror write, then <c>after</c>; either may throw.</summary>
internal sealed class HookedMirror(ILibraryMirror inner, Action? before = null, Action? after = null) : ILibraryMirror
{
    public async Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken)
    {
        before?.Invoke();
        await inner.ApplyAsync(replace, delete, cancellationToken);
        after?.Invoke();
    }
}
