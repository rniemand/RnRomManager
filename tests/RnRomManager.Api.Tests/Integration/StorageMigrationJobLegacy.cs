using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

internal sealed record StorageMigrationJobLegacy(
    InfoJsonGame Game,
    string ZipPath,
    string Zip,
    byte[][] Roms,
    byte[] ZipBytes)
{
    public string Folder => Path.GetDirectoryName(Zip)!;
}
