using System.Text.Json;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Multi-disc M3–M4: a PlayStation game's disc sets in the detail, and <c>/play-disc-set</c>.</summary>
public partial class LibraryEndpointsTests
{
    /// <summary>Disc <paramref name="n"/> of <c>Odyssey (USA)</c> as a standalone image (M1's identifying fields shared).</summary>
    private static InfoJsonRom OdysseyDisc(int n, byte[] bytes, string ext = ".chd") =>
        PsxSolo($"Odyssey (USA) (Disc {n}){ext}", bytes, isPrimary: n == 1) with { BaseTitle = "Odyssey", Regions = ["USA"], Flags = [$"Disc {n}"] };

    /// <summary>Disc <paramref name="n"/> of <c>Odyssey (USA)</c> as a two-track cue set.</summary>
    private static InfoJsonRom OdysseyCueDisc(int n, byte[] track1, byte[] track2)
    {
        var disc = $"Odyssey (USA) (Disc {n})";
        return PsxSet(disc, track1, PsxCue($"{disc} (Track 1).bin", $"{disc} (Track 2).bin"), track2, isPrimary: n == 1)
            with { BaseTitle = "Odyssey", Regions = ["USA"], Flags = [$"Disc {n}"] };
    }

    private static JsonElement DiscSetOf(JsonElement detail, string fileName) => Release(detail, fileName).GetProperty("discSet");

    [Test]
    public async Task Detail_DiscSet_IsOnDiscOne_WithTheNameAndOrderedIds_AndNullElsewhere()
    {
        var (id, _, ids) = await PsxGame("Odyssey",
            [OdysseyDisc(2, Umd(41)), OdysseyCueDisc(1, PsxTrack1, PsxTrack2), OdysseyDisc(3, Umd(42)), PsxSolo("Bonus (USA).chd", Umd(43), isPrimary: false)]);

        var detail = await GetJson($"/api/games/{id}");

        var set = DiscSetOf(detail, "Odyssey (USA) (Disc 1) (Track 1).bin");
        Assert.That(set.GetProperty("name").GetString(), Is.EqualTo("Odyssey (USA).m3u"));
        Assert.That(set.GetProperty("romIds").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new[] { ids[1], ids[0], ids[2] }));
        foreach (var other in new[] { "Odyssey (USA) (Disc 2).chd", "Odyssey (USA) (Disc 3).chd", "Bonus (USA).chd" })
            Assert.That(DiscSetOf(detail, other).ValueKind, Is.EqualTo(JsonValueKind.Null), other);
    }

    [Test]
    public async Task Detail_DiscSet_IsNullForALoneDisc_AndOnAnotherSystem()
    {
        var (lone, _, _) = await PsxGame("Lonely", [OdysseyDisc(1, Umd(44))]);
        var (saturn, _, _) = await SaturnGame("Odyssey Saturn", [OdysseyDisc(1, Umd(45)), OdysseyDisc(2, Umd(46))]);

        Assert.That(DiscSetOf(await GetJson($"/api/games/{lone}"), "Odyssey (USA) (Disc 1).chd").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var saturnDetail = await GetJson($"/api/games/{saturn}");
        Assert.That(DiscSetOf(saturnDetail, "Odyssey (USA) (Disc 1).chd").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(DiscSetOf(saturnDetail, "Odyssey (USA) (Disc 2).chd").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }
}
