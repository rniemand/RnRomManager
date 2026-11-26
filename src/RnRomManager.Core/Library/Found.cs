namespace RnRomManager.Core.Library;

internal sealed record Found(
    string ZipPath,
    Guid Id,
    MirrorEntry? Entry,
    IReadOnlyCollection<string> Sha1s);
