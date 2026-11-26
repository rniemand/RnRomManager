using RnRomManager.Core.Library;

namespace RnRomManager.Core.Media;

/// <summary>
/// P3: a cover download. <see cref="Bytes"/> on a 200 within <see cref="ImageFormats.MaxBytes"/>; else <see cref="Status"/>
/// is the response's status (null for a connection error or timeout) and <see cref="Error"/> says why.
/// </summary>
public sealed record IgdbCoverDownload(byte[]? Bytes, int? Status, string? Error);
