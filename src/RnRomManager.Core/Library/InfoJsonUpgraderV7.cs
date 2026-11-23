using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>v7 → v8 (J2): adds <c>igdbId: null</c> (not linked).</summary>
public sealed class InfoJsonUpgraderV7 : IInfoJsonUpgrader
{
    public int FromVersion => 7;

    public void Upgrade(JsonObject document) => document["igdbId"] = null;
}
