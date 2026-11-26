namespace RnRomManager.Core.Library;

/// <summary>B8–B11: a save's bytes, already streamed to a temp file by the endpoint (size and SHA-1 counted on the way).</summary>
/// <param name="Extension">B1, from the upload's file name.</param>
public sealed record SaveUpload(
    string Path,
    string Extension,
    long SizeBytes,
    string Sha1);
