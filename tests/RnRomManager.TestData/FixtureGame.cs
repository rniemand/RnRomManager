using System.Security.Cryptography;

namespace RnRomManager.TestData;

/// <summary>One game for <see cref="FixtureDatWriter"/>: its DAT names, the ROM bytes (hashed for real) and optional metadat.</summary>
public sealed record FixtureGame(string GameName, string RomName, byte[] Rom)
{
    public int? ReleaseYear { get; init; }

    public string? Serial { get; init; }

    /// <summary>Each value is written as its own metadat block, so a CRC with two developers has two blocks (spec §10.1).</summary>
    public IReadOnlyList<string> Developers { get; init; } = [];

    public IReadOnlyList<string> Publishers { get; init; } = [];

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Precomputed hashes, used instead of hashing <see cref="Rom"/> (see <see cref="FromFile"/>).</summary>
    public FixtureRomHashes? Hashes { get; init; }

    /// <summary>What the DAT records: <see cref="Hashes"/>, or the hashes of <see cref="Rom"/>.</summary>
    public FixtureRomHashes RomHashes => Hashes ?? FixtureRomHashes.Of(Rom);

    /// <summary>
    /// A game for a ROM file on disk, hashed in one streamed read so a 512 MiB file is never held in memory. The game name is
    /// the file name without its extension, the ROM name the file name.
    /// </summary>
    public static FixtureGame FromFile(string path)
    {
        var name = Path.GetFileName(path);
        return new FixtureGame(Path.GetFileNameWithoutExtension(name), name, []) { Hashes = FixtureRomHashes.OfFile(path) };
    }
}

/// <summary>A ROM's size and hashes as a DAT lists them (lower-case hex; the writer upper-cases them).</summary>
public sealed record FixtureRomHashes(long SizeBytes, string Crc32, string Md5, string Sha1)
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
