using System.Net;
using System.Security.Cryptography;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B15: Review on a FileSets system (<see cref="ExternalSetTestHandler"/>).</summary>
public partial class ExternalLibraryTests
{
    private async Task<AuthClient> AdminClientAsync()
    {
        var client = await AuthClient.CreateAsync(_factory);
        var name = "admin" + Guid.NewGuid().ToString("N")[..8];
        await TestUsers.CreateAsync(_factory, name, TestUsers.AllRoles);
        (await client.LoginAsync(name, TestUsers.Password)).Dispose();
        return client;
    }

    private async Task<long> SetItemIdAsync(byte[] bytes) =>
        (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!.Id;

    /// <summary>Ingests an unmatched loose file of the set system, which waits in Review.</summary>
    private async Task<long> QueueSetAsync(string name, byte[] bytes)
    {
        Assert.That(Code(await IngestAsync(Pipeline(), DropSet(name, bytes))), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        return await SetItemIdAsync(bytes);
    }

    private static ReviewResolutionRequest DatPick(string game) =>
        new() { Action = "Dat", DatSystem = ExternalSetTestHandler.SystemId, DatGameName = game };

    [Test]
    public async Task SetReview_BinItem_IsDeleteOnly_OverHttp()
    {
        var track1 = RandomNumberGenerator.GetBytes(2352);
        DropDisc("Mystery Disc.cue", ("Mystery Disc (Track 1).bin", track1), ("Mystery Disc (Track 2).bin", Audio2));
        await RunJobAsync(Pipeline());
        var bin = await SetItemIdAsync(track1);
        var iso = await QueueSetAsync("mystery.iso", RandomNumberGenerator.GetBytes(2000));
        using var admin = await AdminClientAsync();

        using (var resolve = await admin.PostAsync($"/api/review/{bin}/resolve", ManualMystery))
        {
            Assert.That(resolve.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(resolve), Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        }
        using (var dismiss = await admin.PostAsync($"/api/review/{bin}/dismiss", null))
        {
            Assert.That(dismiss.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(dismiss), Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        }
        using (var detail = await admin.GetAsync($"/api/review/{bin}"))
            Assert.That((await AuthClient.JsonAsync(detail)).GetProperty("resolvable").GetBoolean(), Is.False);
        using (var detail = await admin.GetAsync($"/api/review/{iso}"))
            Assert.That((await AuthClient.JsonAsync(detail)).GetProperty("resolvable").GetBoolean(), Is.True);
        Assert.That(await StatusAsync(bin), Is.EqualTo("Pending"));
    }

    [Test]
    public async Task SetReview_IsoWhoseExactNameIsTaken_Is409NameTaken_AndStaysPending()
    {
        await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso));
        File.WriteAllBytes(Path.Combine(SetFolder('D'), "Disc Game (USA) (Unverified).iso"), [9]);
        var id = await QueueSetAsync("mystery.iso", RandomNumberGenerator.GetBytes(2000));
        using var admin = await AdminClientAsync();

        using var response = await admin.PostAsync($"/api/review/{id}/resolve", DatPick("Disc Game (USA)"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(IngestErrors.NameTaken));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(File.Exists(Path.Combine(SetIngest, "mystery.iso")), Is.True);
    }

    [Test]
    public async Task SetReview_NrgPickedForADataTrackGame_IsNamedByTheGame_WithoutATrackFlag()
    {
        var id = await QueueSetAsync("sky.nrg", RandomNumberGenerator.GetBytes(4096));

        var result = await Resolver().ResolveAsync(id, DatPick("Track Game (USA)"), await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var rom = ReadSetGame(Path.Combine(SetFolder('T'), "Track Game.zip")).Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.BaseTitle),
            Is.EqualTo(("Track Game (USA) (Unverified).nrg", "Track Game (USA) (Unverified).nrg", "Track Game")));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Unverified" }));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "USA" }));
    }

    [Test]
    public async Task SetReview_GameNameStartingWithDots_Resolves()
    {
        WriteDats(new FixtureGame("...Iru! (Japan)", "...Iru! (Japan) (Track 1).bin", RandomNumberGenerator.GetBytes(2352)));
        var id = await QueueSetAsync("iru.nrg", RandomNumberGenerator.GetBytes(4096));

        var result = await Resolver().ResolveAsync(id, DatPick("...Iru! (Japan)"), await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var zip = Directory.GetFiles(Path.Combine(_factory.Root, "roms", ExternalSetTestHandler.SystemId), "*.zip", SearchOption.AllDirectories).Single();
        Assert.That(ReadSetGame(zip).Roms.Single().ExternalFileName, Is.EqualTo("...Iru! (Japan) (Unverified).nrg"));
    }

    [Test]
    public async Task SetReview_AttachAmbiguousIsoWithFlagNone_TakesItsTagsFromTheDatGameName()
    {
        await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso));
        await IngestAsync(Pipeline(), DropSet("single.bin", SingleBin));
        await SqlAsync("UPDATE roms SET base_title = 'Disc Game'"); // Disc Game and Single Game both own "Disc Game"
        var rev1 = DropSet("rev1.iso", DiscRev1);
        Assert.That(Code(await IngestAsync(Pipeline(), rev1)), Is.EqualTo((IngestOutcome.Review, (string?)"AmbiguousGroup")));
        var single = (await Service<RnRomManager.Core.Library.IGameRepository>().ListAsync(new GameQuery(), default)).Items.Single(g => g.DisplayTitle == "Single Game");
        var request = new ReviewResolutionRequest { Action = "Attach", TargetGameId = single.Id, Flag = "None" };

        var result = await Resolver().ResolveAsync(await SetItemIdAsync(DiscRev1), request, await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var rom = ReadSetGame(Path.Combine(SetFolder('S'), "Single Game.zip")).Roms.Single(r => r.FileName == "Disc Game (USA).iso");
        Assert.That((rom.ExternalFileName, rom.Revision), Is.EqualTo(("Disc Game (USA).iso", "Rev 1")), "Rev 1 is in the game name, not the rom name");
    }
}
