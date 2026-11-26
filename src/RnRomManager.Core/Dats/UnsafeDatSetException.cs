namespace RnRomManager.Core.Dats;

internal sealed class UnsafeDatSetException(string message, bool liveUnsafe) : Exception(message)
{
    public bool LiveUnsafe { get; } = liveUnsafe;
}
