namespace RnRomManager.Core.Ingest;

/// <summary>D5: a restored Deleted ROM under its new pending name in <c>ingest/&lt;system&gt;/</c>.</summary>
public sealed record RestoredFile(string System, string Name);
