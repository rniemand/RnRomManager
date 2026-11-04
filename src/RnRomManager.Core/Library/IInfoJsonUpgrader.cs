using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>
/// One step of the <c>info.json</c> schema chain (spec §6.2): turns a v<see cref="FromVersion"/> document into
/// v<see cref="FromVersion"/>+1 in memory (including <c>schemaVersion</c>). v1 is the first schema, so none exist yet.
/// </summary>
public interface IInfoJsonUpgrader
{
    int FromVersion { get; }

    void Upgrade(JsonObject document);
}
