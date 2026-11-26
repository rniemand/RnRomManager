using System.Security.Cryptography;

namespace RnRomManager.Core.Ingest;

internal sealed record RomHashes(string Crc32, string Md5, string Sha1)
{
    public static RomHashes Of(byte[] bytes) =>
        new(Storage.Crc32.ToHex(bytes), Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA1.HashData(bytes)));
}
