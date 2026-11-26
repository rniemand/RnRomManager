using System.Security.Cryptography;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Media;

/// <summary>A box art or snap ready to store as <c>images/&lt;fileName&gt;</c>.</summary>
/// <param name="Source">The <c>info.json</c> source of a fetched image; null means libretro-thumbnails.</param>
public sealed record MediaImage(
    ImageType Type,
    byte[] Bytes,
    int Width,
    int Height,
    string? SourceUrl,
    bool IsDefault,
    string? Source = null)
{
    public const string BoxArtFileName = "boxart-01.png";
    public const string SnapFileName = "snap-01.png";

    /// <summary>T8: <c>png</c> or <c>jpg</c>, from the magic bytes.</summary>
    public string Extension => ImageFormats.Extension(ImageFormats.Detect(Bytes) ?? ImageFormat.Png);

    /// <summary>The name of a new game's first image of this type, by format (<c>boxart-01.jpg</c>).</summary>
    public string FileName => $"{GameImages.Prefix(Type)}-01.{Extension}";

    public InfoJsonImage ToInfoJson(DateTimeOffset dateAdded, bool isCover) => new()
    {
        FileName = FileName,
        Type = Type,
        Source = IsDefault ? InfoJsonImage.SourceDefault : Source ?? InfoJsonImage.SourceLibretro,
        SourceUrl = SourceUrl,
        IsDefault = IsDefault,
        IsCover = isCover,
        SizeBytes = Bytes.Length,
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(Bytes)),
        Width = Width,
        Height = Height,
        DateAdded = dateAdded,
    };
}
