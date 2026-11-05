using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>v1 → v2: adds <c>rating: null</c> and <c>favourite: false</c>.</summary>
public sealed class InfoJsonUpgraderV1 : IInfoJsonUpgrader
{
    public int FromVersion => 1;

    public void Upgrade(JsonObject document)
    {
        document["rating"] = null;
        document["favourite"] = false;
    }
}
