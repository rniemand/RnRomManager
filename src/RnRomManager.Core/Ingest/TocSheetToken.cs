namespace RnRomManager.Core.Ingest;

/// <summary>A word, a string's value (<paramref name="Raw"/> keeps its quotes), a line end, or a tokenizing error.</summary>
internal readonly record struct TocSheetToken(
    Kind Kind,
    string Text,
    string Raw,
    int Line);
