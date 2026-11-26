using System.Security.Cryptography;

namespace RnRomManager.TestData;

/// <summary>A ROM's size and hashes as a DAT lists them (lower-case hex; the writer upper-cases them).</summary>
public sealed record FixtureRomHashes(
    long SizeBytes,
    string Crc32,
    string Md5,
    string Sha1)
{
    public static FixtureRomHashes Of(byte[] rom) => new(rom.Length, Core.Storage.Crc32.ToHex(rom),
        Convert.ToHexStringLower(MD5.HashData(rom)), Convert.ToHexStringLower(SHA1.HashData(rom)));

    public static FixtureRomHashes OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc = Core.Storage.Crc32.Initial;
        var buffer = new byte[1024 * 1024];
        long size = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            var chunk = buffer.AsSpan(0, read);
            crc = Core.Storage.Crc32.Update(crc, chunk);
            md5.AppendData(chunk);
            sha1.AppendData(chunk);
            size += read;
        }
        return new FixtureRomHashes(size, Core.Storage.Crc32.Finish(crc).ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()));
    }
}
