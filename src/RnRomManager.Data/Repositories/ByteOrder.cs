namespace RnRomManager.Data.Repositories;

internal sealed class ByteOrder : IComparer<byte[]>
{
    public static readonly ByteOrder Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}
