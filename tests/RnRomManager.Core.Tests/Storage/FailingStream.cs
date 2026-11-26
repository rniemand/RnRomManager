namespace RnRomManager.Core.Tests.Storage;

internal sealed class FailingStream(Stream inner) : MemoryStream
{
    private bool _served;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_served)
            throw new IOException("disk full");
        _served = true;
        return inner.Read(buffer, offset, 3);
    }
}
