using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Bundle Creator B3: durable identities resolved through the live mirror.</summary>
[Category("Integration")]
public class BundleFileResolutionTests
{
    private static readonly DateTime Mtime = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private ILibraryMirror _mirror = null!;
    private RomRepository _roms = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _mirror = new LibraryMirror(factory);
        _roms = new RomRepository(factory);
    }

    private static InfoJsonRomFile File(string name, int seed)
    {
        var bytes = RomBytes(seed);
        return new InfoJsonRomFile(name, name, name, bytes.Length, Crc32.ToHex(bytes),
            Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    private static InfoJsonRom Disc(string title, int seed, params InfoJsonRomFile[] files)
    {
        var track = $"{title} (Track 1).bin";
        return Rom(track, RomBytes(seed)) with { ExternalFileName = track, Files = files };
    }

    private static InfoJsonGame SetGame(string title, params InfoJsonRom[] discs) =>
        Game(title, discs) with { System = ExternalSetTestHandler.SystemId };

    private Task MirrorAsync(InfoJsonGame game, string zip) =>
        _mirror.ReplaceAsync(new MirrorEntry(game, zip, 1234, Mtime), CancellationToken.None);

    private Task<IReadOnlyList<BundleFileTarget?>> ResolveAsync(params BundleFileIdentity[] identities) =>
        _roms.ResolveBundleFilesAsync(identities, CancellationToken.None);

    private static BundleFileIdentity MainOf(InfoJsonGame game, int rom = 0) => new(game.GameId, game.Roms[rom].Metadata.Sha1, null);

    private static BundleFileIdentity MemberOf(InfoJsonGame game, int file, int rom = 0, string? name = null)
    {
        var f = game.Roms[rom].Files![file];
        return new(game.GameId, game.Roms[rom].Metadata.Sha1, new BundleMember(f.Sha1, name ?? f.FileName));
    }

    [Test]
    public async Task MainAndSetFiles_ResolveToTheirCurrentTargets_InInputOrder()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA)", 1, File("Disc Runner (USA).cue", 101), File("Disc Runner (USA) (Track 2).bin", 50)));
        var cart = Game("Orbit Blocks", Rom("Orbit Blocks (World).gb", RomBytes(7)));
        await MirrorAsync(game, "extset/D/Disc Runner.zip");
        await MirrorAsync(cart, "gb/O/Orbit Blocks.zip");
        var rom = (await _roms.ListByGameAsync(game.GameId, default)).Single();
        var files = await _roms.ListFilesByGameAsync(game.GameId, default);
        var cartRom = (await _roms.ListByGameAsync(cart.GameId, default)).Single();

        var targets = await ResolveAsync(MemberOf(game, 1), MainOf(cart), MainOf(game), MemberOf(game, 0));

        Assert.That(targets, Is.EqualTo(new BundleFileTarget?[]
        {
            new(game.GameId, "Disc Runner", rom.Id, files[1].Id, "Disc Runner (USA) (Track 2).bin"),
            new(cart.GameId, "Orbit Blocks", cartRom.Id, null, "Orbit Blocks (World).gb"),
            new(game.GameId, "Disc Runner", rom.Id, null, "Disc Runner (USA) (Track 1).bin"),
            new(game.GameId, "Disc Runner", rom.Id, files[0].Id, "Disc Runner (USA).cue"),
        }));
        Assert.That(targets[0]!.DownloadUrl, Is.EqualTo($"/api/games/{game.GameId}/roms/{rom.Id}/files/{files[1].Id}/download"));
        Assert.That(targets[1]!.DownloadUrl, Is.EqualTo($"/api/games/{cart.GameId}/roms/{cartRom.Id}/download"));
    }

    [Test]
    public async Task AMirrorRewrite_ChangesNumericIds_ButResolvesTheSameFiles()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA)", 1, File("Disc Runner (USA).cue", 101), File("Disc Runner (USA) (Track 2).bin", 50)));
        await MirrorAsync(game, "extset/D/Disc Runner.zip");
        var before = await ResolveAsync(MainOf(game), MemberOf(game, 1));

        await MirrorAsync(game, "extset/D/Disc Runner.zip");
        var after = await ResolveAsync(MainOf(game), MemberOf(game, 1));

        Assert.That(after[0]!.RomId, Is.Not.EqualTo(before[0]!.RomId));
        Assert.That(after[1]!.FileId, Is.Not.EqualTo(before[1]!.FileId));
        var rom = (await _roms.ListByGameAsync(game.GameId, default)).Single();
        var files = await _roms.ListFilesByGameAsync(game.GameId, default);
        Assert.That((after[0]!.RomId, after[1]!.RomId, after[1]!.FileId), Is.EqualTo((rom.Id, rom.Id, (long?)files[1].Id)));
        Assert.That(after.Select(t => t!.FileName), Is.EqualTo(before.Select(t => t!.FileName)));
    }

    [Test]
    public async Task AReleaseMovedToAnotherGame_IsUnavailable_NeverSubstituted()
    {
        var release = Rom("Orbit Blocks (World).gb", RomBytes(7));
        var from = Game("Orbit Blocks", release);
        await MirrorAsync(from, "gb/O/Orbit Blocks.zip");
        var identity = MainOf(from);

        await _mirror.ApplyAsync([], [from.GameId], CancellationToken.None);
        var to = Game("Orbit Blocks Deluxe", release);
        await MirrorAsync(to, "gb/O/Orbit Blocks Deluxe.zip");

        var targets = await ResolveAsync(identity, MainOf(to));

        Assert.That(identity.ReleaseSha1, Is.EqualTo(MainOf(to).ReleaseSha1));
        Assert.That(targets[0], Is.Null, "the old game's entry doesn't follow the release");
        Assert.That(targets[1]!.GameId, Is.EqualTo(to.GameId));
    }

    [Test]
    public async Task DeletedReleases_AndUnknownHashes_AreUnavailable()
    {
        var game = Game("Orbit Blocks", Rom("Orbit Blocks (World).gb", RomBytes(7)), Rom("Orbit Blocks (Japan).gb", RomBytes(8), isPrimary: false));
        await MirrorAsync(game, "gb/O/Orbit Blocks.zip");
        var gone = MainOf(game, 1);
        await MirrorAsync(game with { Roms = [game.Roms[0]] }, "gb/O/Orbit Blocks.zip");

        var targets = await ResolveAsync(gone, new BundleFileIdentity(game.GameId, new string('a', 40), null), MainOf(game));

        Assert.That(targets.Select(t => t is null), Is.EqualTo(new[] { true, true, false }));
    }

    [Test]
    public async Task ARenamedOrMissingSetFile_IsUnavailable()
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA)", 1, File("Disc Runner (USA).cue", 101), File("Disc Runner (USA) (Track 2).bin", 50)));
        await MirrorAsync(game, "extset/D/Disc Runner.zip");
        var track2 = MemberOf(game, 1);
        var renamed = game with
        {
            Roms = [game.Roms[0] with { Files = [game.Roms[0].Files![0], File("Disc Runner (USA) (Track 02).bin", 50)] }],
        };

        await MirrorAsync(renamed, "extset/D/Disc Runner.zip");

        Assert.That(await ResolveAsync(track2, MemberOf(renamed, 1), MemberOf(game, 0)), Has.Exactly(1).Null);
        Assert.That((await ResolveAsync(track2))[0], Is.Null);
        Assert.That((await ResolveAsync(new BundleFileIdentity(game.GameId, game.Roms[0].Metadata.Sha1, new BundleMember(new string('b', 40), "Disc Runner (USA).cue"))))[0],
            Is.Null, "right name, wrong hash");
    }

    [Test]
    public async Task SameNamedFiles_InOtherReleases_ResolveSeparately_AndDuplicatesAreAmbiguous()
    {
        // The same silent track (seed 77) under the same name in two discs of one game, and twice within one disc.
        var game = SetGame("Twin",
            Disc("Twin (Disc 1)", 1, File("Track 2.bin", 77)),
            Disc("Twin (Disc 2)", 2, File("Track 2.bin", 77)) with { IsPrimary = false },
            Disc("Twin (Disc 3)", 3, File("Track 2.bin", 77), File("Track 2.bin", 77)) with { IsPrimary = false });
        await MirrorAsync(game, "extset/T/Twin.zip");
        var files = await _roms.ListFilesByGameAsync(game.GameId, default);

        var targets = await ResolveAsync(MemberOf(game, 0, 0), MemberOf(game, 0, 1), MemberOf(game, 0, 2));

        Assert.That(targets[0]!.FileId, Is.EqualTo(files[0].Id));
        Assert.That(targets[1]!.FileId, Is.EqualTo(files[1].Id));
        Assert.That(targets[0]!.RomId, Is.Not.EqualTo(targets[1]!.RomId));
        Assert.That(targets[2], Is.Null, "two matches in one release are ambiguous");
    }

    [TestCase("disc runner (usa).cue", TestName = "SetFileNames_CompareExactly(case)")]
    [TestCase("Disc Runnér (USA).cue", TestName = "SetFileNames_CompareExactly(accent)")]
    [TestCase("Disc Runner (USA).cue ", TestName = "SetFileNames_CompareExactly(trailing space)")]
    public async Task SetFileNames_CompareExactly(string variant)
    {
        var game = SetGame("Disc Runner", Disc("Disc Runner (USA)", 1, File("Disc Runner (USA).cue", 101)));
        await MirrorAsync(game, "extset/D/Disc Runner.zip");

        var targets = await ResolveAsync(MemberOf(game, 0, name: variant), MemberOf(game, 0));

        Assert.That(targets[0], Is.Null);
        Assert.That(targets[1], Is.Not.Null);
    }

    [Test]
    public async Task AnEmptyBatch_IsEmpty()
    {
        Assert.That(await ResolveAsync(), Is.Empty);
    }
}
