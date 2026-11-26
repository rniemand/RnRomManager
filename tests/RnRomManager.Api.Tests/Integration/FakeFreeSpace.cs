using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Tests.Integration;

internal sealed class FakeFreeSpace(Func<string, long?> free) : IFreeSpace
{
    public long? AvailableBytes(string path) => free(path);
}
