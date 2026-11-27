using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Reports;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>RP8: the duplicates query's SQL-side games key, and (D1/D2) IGDB groups and dismissals per match, against MariaDB.</summary>
[Category("Integration")]
public class DuplicateQueryTests
{
    private MySqlConnectionFactory _factory = null!;
    private ReportRepository _reports = null!;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(_factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _reports = new ReportRepository(_factory);
    }

    private async Task<Guid> Add(
        string title,
        int? igdbId = null,
        string system = "gb",
        string? coverSha1 = null)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with
        {
            System = system,
            IgdbId = igdbId,
            Images = coverSha1 is null ? [] : [new InfoJsonImage
            {
                FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceDefault, IsDefault = true, IsCover = true,
                SizeBytes = 10, Sha1 = coverSha1, Width = 512, Height = 512, DateAdded = When,
            }],
            Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
        };
        var name = FileNameSanitizer.Sanitize(title) + ".zip";
        await ((ILibraryMirror)new LibraryMirror(_factory)).ReplaceAsync(new MirrorEntry(game, $"{system}/{Bucket.For(name)}/{name}", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    [Test]
    public async Task SqlGamesKey_EqualsDuplicateGroupsGamesKey()
    {
        var ids = new[] { await Add("Quest A", coverSha1: new string('b', 40)), await Add("Quest B"), await Add("Quest C") };
        await using (var connection = _factory.Create())
            await connection.ExecuteAsync("UPDATE roms SET base_title = 'Quest'");

        var page = await _reports.ListDuplicatesAsync(DuplicateMatch.Title, null, 1, 25, default);

        var group = page.Items.Single();
        Assert.That(group.Games.ToDictionary(g => g.Id, g => g.BoxArtSha1),
            Is.EquivalentTo(new Dictionary<Guid, string?> { [ids[0]] = new string('b', 40), [ids[1]] = null, [ids[2]] = null }));
        Assert.That(group.GamesKey, Is.EqualTo(DuplicateGroups.GamesKey(ids)));
        Assert.That(group.GamesKey, Is.EqualTo(DuplicateGroups.GamesKey(ids.Reverse())));
        Assert.That(group.Games.Select(g => g.Id), Is.EquivalentTo(ids));
    }

    [Test]
    public async Task IgdbGroups_ShareAnIdInOneSystem_ZeroAndNullIgnored_SmallestTitleShown()
    {
        var orbit = new[] { await Add("Orbit Kart", 42), await Add("Kart Orbit (USA)", 42) };
        await Add("Other system", 42, "gbc");
        var zelda = new[] { await Add("Zelda", 7), await Add("Zelda DX", 7) };
        await Add("Zero A", 0);
        await Add("Zero B", 0);
        await Add("None A");
        await Add("None B");

        var page = await _reports.ListDuplicatesAsync(DuplicateMatch.Igdb, null, 1, 25, default);

        Assert.That(page.Items.Select(g => (g.SystemId, g.BaseTitle, g.IgdbId)),
            Is.EqualTo(new[] { ("gb", "Kart Orbit (USA)", (int?)42), ("gb", "Zelda", (int?)7) }));
        Assert.That(page.Items[0].GamesKey, Is.EqualTo(DuplicateGroups.GamesKey(orbit)));
        Assert.That(page.Items[1].Games.Select(g => g.Id), Is.EquivalentTo(zelda));
        Assert.That(page.Total, Is.EqualTo(2));
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Title, null, 1, 25, default)).Items.Select(g => g.IgdbId), Is.All.Null);
    }

    [Test]
    public async Task Dismissals_ArePerMatch()
    {
        var ids = new[] { await Add("Quest A", 42), await Add("Quest B", 42) };
        await using (var connection = _factory.Create())
            await connection.ExecuteAsync("UPDATE roms SET base_title = 'Quest'");
        var key = DuplicateGroups.GamesKey(ids);

        await _reports.DismissDuplicateAsync(DuplicateMatch.Igdb, "gb", "42", key, null, When.UtcDateTime, default);
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Igdb, null, 1, 25, default)).Total, Is.Zero);
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Title, null, 1, 25, default)).Total, Is.EqualTo(1));

        await _reports.DismissDuplicateAsync(DuplicateMatch.Title, "gb", "Quest", key, null, When.UtcDateTime, default);
        await _reports.DismissDuplicateAsync(DuplicateMatch.Title, "gb", "Quest", key, null, When.UtcDateTime, default);
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Title, null, 1, 25, default)).Total, Is.Zero);
    }

    /// <summary>D2: adds a set file to each game's release with the given name and hashes.</summary>
    private async Task AddSetFile(
        Guid gameId,
        string fileName,
        string sha1,
        string md5)
    {
        await using var connection = _factory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO rom_files (rom_id, game_id, ordinal, file_name, external_file_name, original_file_name, size_bytes, crc32, md5, sha1)
            SELECT r.id, r.game_id, 1, @fileName, @fileName, @fileName, 10, '00000000', @md5, @sha1 FROM roms r WHERE r.game_id = @gameId
            """, new { gameId, fileName, sha1, md5 });
    }

    [Test]
    public async Task HashGroups_SharedSetFileSha1_ListedOnce_SmallestName_PerSystem()
    {
        var sha1 = new string('c', 40);
        var md5 = new string('d', 32);
        var a = await Add("Wipeout");
        var b = await Add("Wipeout XL");
        var other = await Add("Wipeout GBC", system: "gbc");
        await AddSetFile(a, "Track 03.bin", sha1, md5);
        await AddSetFile(b, "Track 02.bin", sha1, md5);
        await AddSetFile(other, "Track 01.bin", sha1, md5);

        var page = await _reports.ListDuplicatesAsync(DuplicateMatch.Hash, null, 1, 25, default);

        // One identical file: the SHA1 group only, not an MD5 group too; the GBC game alone isn't a group.
        var group = page.Items.Single();
        Assert.That((group.SystemId, group.BaseTitle, group.HashKind, group.Hash), Is.EqualTo(("gb", "Track 02.bin", "sha1", sha1)));
        Assert.That(group.GamesKey, Is.EqualTo(DuplicateGroups.GamesKey([a, b])));
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Hash, "gbc", 1, 25, default)).Items, Is.Empty);
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Title, null, 1, 25, default)).Items.Select(g => g.HashKind), Is.All.Null);
    }

    [Test]
    public async Task HashGroups_Md5Group_OnlyWhenTheSha1sDiffer_AndDismissalHidesOnlyThatGroup()
    {
        var md5 = new string('e', 32);
        var a = await Add("Alpha");
        var b = await Add("Bravo");
        await using (var connection = _factory.Create())
            await connection.ExecuteAsync("UPDATE roms SET md5 = @md5", new { md5 });
        await AddSetFile(a, "Track 02.bin", new string('f', 40), new string('1', 32));
        await AddSetFile(b, "Track 02.bin", new string('f', 40), new string('1', 32));

        var groups = (await _reports.ListDuplicatesAsync(DuplicateMatch.Hash, null, 1, 25, default)).Items;
        Assert.That(groups.Select(g => (g.BaseTitle, g.HashKind)), Is.EqualTo(new[] { ("Alpha.gb", "md5"), ("Track 02.bin", "sha1") }));

        await _reports.DismissDuplicateAsync(DuplicateMatch.Hash, "gb", "md5:" + md5, groups[0].GamesKey, null, DateTime.UtcNow, default);
        Assert.That((await _reports.ListDuplicatesAsync(DuplicateMatch.Hash, null, 1, 25, default)).Items.Select(g => g.HashKind),
            Is.EqualTo(new[] { "sha1" }));
    }}
