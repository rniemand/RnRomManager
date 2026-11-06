using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>v2 → v3: every image gets <c>isCover</c>; the BoxArt image is the cover, else the first image, else none.</summary>
public sealed class InfoJsonUpgraderV2 : IInfoJsonUpgrader
{
    public int FromVersion => 2;

    public void Upgrade(JsonObject document)
    {
        if (document["images"] is not JsonArray images)
            return; // the typed read reports the missing list
        var objects = images.OfType<JsonObject>().ToList();
        var cover = objects.FirstOrDefault(i => i["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == nameof(ImageType.BoxArt))
            ?? objects.FirstOrDefault();
        foreach (var image in objects)
            image["isCover"] = ReferenceEquals(image, cover);
    }
}
