using System.Text.Json;
using System.Text.Json.Nodes;
using RnRomManager.Core.Library;

namespace RnRomManager.TestData;

/// <summary>Release editing B16: hand-written version 1 <c>saves.json</c> files (the app now only writes version 2).</summary>
public static class TestSaves
{
    /// <summary>
    /// A version 1 index listing <paramref name="saves"/> as they are (a null <c>uploadedBy</c> included); their bytes belong at
    /// <c>saves/&lt;id&gt;&lt;extension&gt;</c>, the entry of a record with <see cref="GameSave.Legacy"/> set.
    /// </summary>
    public static byte[] V1Index(params GameSave[] saves)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var index = new JsonObject
        {
            ["version"] = 1,
            ["saves"] = new JsonArray([.. saves.Select(s => JsonSerializer.SerializeToNode(s, options))]),
        };
        return JsonSerializer.SerializeToUtf8Bytes(index, options);
    }
}
