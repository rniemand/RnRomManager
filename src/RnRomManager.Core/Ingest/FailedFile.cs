namespace RnRomManager.Core.Ingest;

/// <summary>D1: a file in <c>ingest/&lt;system&gt;/_failed/</c>; <see cref="Reason"/> comes from its <c>.error.txt</c>.</summary>
public sealed record FailedFile(
    string Name,
    long SizeBytes,
    DateTime ModifiedAtUtc,
    string? Reason);
