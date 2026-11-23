using System.IO.Compression;

namespace RnRomManager.Systems.Commodore;

/// <summary>Inflates a gzipped disk image (Amiga <c>.adz</c>, VICE <c>.d6z</c>, <c>.gz</c>, …) so the image itself is hashed and stored.</summary>
internal static class GzipImage
{
    /// <summary>
    /// A gzip stream (recognised by its magic) is inflated. Input without the magic, a corrupt stream, or one inflating past
    /// <paramref name="maxBytes"/> is returned unchanged: it matches nothing and goes to Review.
    /// </summary>
    public static ReadOnlyMemory<byte> Inflate(ReadOnlyMemory<byte> rom, long maxBytes)
    {
        if (rom.Length < 2 || rom.Span[0] != 0x1F || rom.Span[1] != 0x8B)
            return rom;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(rom.ToArray(), writable: false), CompressionMode.Decompress);
            using var image = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (image.Length + read > maxBytes)
                    return rom;
                image.Write(buffer, 0, read);
            }
            return image.ToArray();
        }
        catch (InvalidDataException)
        {
            return rom;
        }
    }
}
