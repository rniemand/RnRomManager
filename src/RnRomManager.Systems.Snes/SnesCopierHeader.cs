namespace RnRomManager.Systems.Snes;

/// <summary>SNES spec S3: copier (SMC/SWC) dumps carry a 512-byte header before the image; No-Intro hashes without it.</summary>
public static class SnesCopierHeader
{
    public const int Size = 512;

    /// <summary>Drops the first 512 bytes when <c>length % 1024 == 512</c> (by size only, not extension).</summary>
    public static ReadOnlyMemory<byte> Strip(ReadOnlyMemory<byte> rom) => rom.Length % 1024 == Size ? rom[Size..] : rom;
}
