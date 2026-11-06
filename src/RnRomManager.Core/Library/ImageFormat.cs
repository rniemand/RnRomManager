namespace RnRomManager.Core.Library;

/// <summary>Image formats a library ZIP may hold (info.json v3): PNG and JPEG, stored as-is.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
}

/// <summary>Format detection by magic bytes (never by file name) and per-format facts.</summary>
public static class ImageFormats
{
    /// <summary>Largest image we store, upload or serve (I1a). Thumbnail fetches have their own, lower limit.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageFormat? Detect(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature) ? ImageFormat.Png
        : bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? ImageFormat.Jpeg
        : null;

    /// <summary>File extension without the dot: <c>png</c> or <c>jpg</c>.</summary>
    public static string Extension(ImageFormat format) => format == ImageFormat.Png ? "png" : "jpg";

    /// <summary>By stored file name: <c>.jpg</c>/<c>.jpeg</c> → JPEG, anything else PNG.</summary>
    public static string ContentType(string fileName) =>
        fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

    /// <returns>Format and dimensions, or null when it isn't a PNG/JPEG within <see cref="MaxBytes"/> whose size we can read.</returns>
    public static (ImageFormat Format, int Width, int Height)? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
            return null;
        switch (Detect(bytes))
        {
            case ImageFormat.Png when PngInfo.Read(bytes) is { } png:
                return (ImageFormat.Png, png.Width, png.Height);
            case ImageFormat.Jpeg when JpegInfo.Read(bytes) is { } jpeg:
                return (ImageFormat.Jpeg, jpeg.Width, jpeg.Height);
            default:
                return null;
        }
    }
}
