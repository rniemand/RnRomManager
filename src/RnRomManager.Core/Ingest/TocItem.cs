namespace RnRomManager.Core.Ingest;

/// <summary>
/// One data item of a <c>.toc</c> track. <paramref name="Offset"/> is the <c>#&lt;bytes&gt;</c> offset; the start and
/// length are in sectors, a null length meaning "to the end of the file".
/// </summary>
public sealed record TocItem(
    string FileName,
    TocItemKind Kind,
    bool Swap,
    long? Offset,
    int? Start,
    int? Length);
