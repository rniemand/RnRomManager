using System.Globalization;
using System.Security.Cryptography;

namespace RnRomManager.Core.Storage;

/// <summary>What one streamed read of a ROM file found. Hashes are lowercase hex.</summary>
/// <param name="Header">The first <c>min(size, <see cref="RomFileHasher.HeaderBytes"/>)</c> bytes, for <c>ReadHeader</c>.</param>
public sealed record RomFileHashes(long SizeBytes, string Crc32, string Md5, string Sha1, byte[] Header);

/// <summary>
/// Hashes a ROM too large to hold in memory (spec I2): one pass computes CRC32, MD5 and SHA1 and keeps only the
/// start of the file for the system's header reader.
/// </summary>
public static class RomFileHasher
{
    public const int HeaderBytes = 1024 * 1024;

    private const int BufferBytes = 1024 * 1024;

    /// <param name="stream">The caller's checked stream, read from its current position to its end; not disposed.</param>
    /// <param name="maxBytes">The system's <c>MaxRomSizeBytes</c>.</param>
    /// <param name="onProgress">Called after each buffer with the bytes read so far (spec B2, job step progress).</param>
    /// <returns>
    /// null when the stream holds more than <paramref name="maxBytes"/> (a file that grew past the cap while it was
    /// read); reading stops there.
    /// </returns>
    public static async Task<RomFileHashes?> HashAsync(Stream stream, long maxBytes, CancellationToken cancellationToken,
        Action<long>? onProgress = null)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc = Crc32.Initial;
        var header = new MemoryStream();
        var buffer = new byte[BufferBytes];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            size += read;
            if (size > maxBytes)
                return null;
            crc = Crc32.Update(crc, buffer.AsSpan(0, read));
            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            if (header.Length < HeaderBytes)
                header.Write(buffer, 0, (int)Math.Min(read, HeaderBytes - header.Length));
            onProgress?.Invoke(size);
        }

        return new RomFileHashes(size, Crc32.Finish(crc).ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()), header.ToArray());
    }
}
