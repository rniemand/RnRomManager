namespace RnRomManager.Core.Library;

/// <summary>
/// v6 (B12): one more file of a release on a <c>FileSets</c> system: the cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c>, another track, or a CloneCD <c>.sub</c>. It lies beside the ZIP
/// under <see cref="ExternalFileName"/>, which equals <see cref="FileName"/>. Hashes are lowercase hex.
/// </summary>
public sealed record InfoJsonRomFile(
    string FileName,
    string ExternalFileName,
    string OriginalFileName,
    long SizeBytes,
    string Crc32,
    string Md5,
    string Sha1);
