using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>
/// v6 → v7 (B7): v7 only allows <c>Serial</c> as an <c>identification.method</c>, so a v6 document needs no change
/// (the reader sets the version).
/// </summary>
public sealed class InfoJsonUpgraderV6 : IInfoJsonUpgrader
{
    public int FromVersion => 6;

    public void Upgrade(JsonObject document)
    {
    }
}
