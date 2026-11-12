using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec T3: a titledb system's store, and the manifest and compact checks that disable only that system.</summary>
public class TitleDbCacheTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Db = ExternalTitleDbTestHandler.DatNameValue;
    private static readonly string Compact = TitleDb.RelativePath(Db);

    private TempDir _dir = null!;
    private DatCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var gb = Substitute.For<ISystemHandler>();
        gb.EmbedRomsInZip.Returns(true);
        gb.Id.Returns("gb");
        gb.DatName.Returns(Gb);
        gb.DatFolder.Returns(DatKind.NoIntro);
        gb.MetadatKinds.Returns(DatKind.Metadat);
        gb.RomExtensions.Returns([".gb"]);
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns([gb, new ExternalTitleDbTestHandler()]);
        _cache = new DatCache(Options.Create(new PathsOptions { Cache = _dir.Path }), registry, NullLogger<DatCache>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static TitleDbRow Row(string id, string name) => new(id, name, "Pub", null, 2020, ["Action"], ["en"], null, null);

    private static readonly TitleDbRow[] Rows =
    [
        Row("0100000000001000", "The Legend of Zelda: Tears of the Kingdom"),
        Row("0100000000002000", "The Legend of Zelda: Tears of the Kingdom"),
        Row("0100000000003000", "Oh...Sir! The Insult Simulator"),
    ];

    private void Write(IReadOnlyList<TitleDbRow>? rows = null) => FixtureDatWriter.Write(_cache.Directory,
        new Dictionary<string, IReadOnlyList<FixtureGame>> { [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", TestLibrary.RomBytes(1))] },
        titleDb: new Dictionary<string, IReadOnlyList<TitleDbRow>> { [Db] = rows ?? Rows });

    private string ManifestPath => Path.Combine(_cache.Directory, DatManifest.FileName);

    private void EditManifest(Func<List<DatManifestEntry>, List<DatManifestEntry>> change) =>
        DatManifest.Write(ManifestPath, change([.. DatManifest.Read(ManifestPath)]));

    private static DatManifestEntry Titledb(List<DatManifestEntry> entries) => entries.Single(e => e.File == Compact);

    /// <summary>Replaces the compact file's text and, unless told not to, its manifest SHA1.</summary>
    private void RewriteCompact(string json, bool fixSha1 = true)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Path.Combine(_cache.Directory, Compact), bytes);
        if (fixSha1)
            EditManifest(e => [.. e.Select(x => x.File == Compact ? x with { Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)) } : x)]);
    }

    private void AssertOnlyTitleDbDisabled()
    {
        _cache.Load();
        Assert.That(_cache.For(ExternalTitleDbTestHandler.SystemId), Is.Null);
        Assert.That(_cache.For("gb"), Is.Not.Null);
    }

    [Test]
    public void Store_HasOneUniqueGameNamePerEntry_WithoutSizesOrHashes()
    {
        Write();

        _cache.Load();

        var store = _cache.For(ExternalTitleDbTestHandler.SystemId)!;
        Assert.That((store.DatName, store.Version), Is.EqualTo((DatStore.TitleDbName, FixtureDatWriter.TitleDbVersion)));
        Assert.That(store.Roms.Select(r => r.GameName), Is.EqualTo(new[]
        {
            "Legend of Zelda, The - Tears of the Kingdom [0100000000001000]",
            "Legend of Zelda, The - Tears of the Kingdom [0100000000002000]",
            "Oh...Sir! The Insult Simulator [0100000000003000]",
        }));
        Assert.That(store.Roms[2].RomName, Is.EqualTo("Oh…Sir! The Insult Simulator [0100000000003000][v0].nsp"));
        Assert.That(store.Roms.Select(r => (r.SizeBytes, r.Crc32, r.Md5, r.Sha1)), Is.All.EqualTo((0L, "", "", "")));
        Assert.That(store.BySha1(""), Is.Null, "no hash index entry");
        Assert.That(store.ByTitleId("0100000000003000")!.Name, Is.EqualTo("Oh...Sir! The Insult Simulator"));
        Assert.That(store.ByTitleId("0100000000009000"), Is.Null);
    }

    [Test]
    public void ZeroEntries_LoadAnEmptyStore()
    {
        Write([]);

        _cache.Load();

        Assert.That(_cache.For(ExternalTitleDbTestHandler.SystemId)!.Roms, Is.Empty);
    }

    [Test]
    public void NullManifestRow_IsAMissingCache()
    {
        Write();
        var text = File.ReadAllText(ManifestPath);
        File.WriteAllText(ManifestPath, text.TrimEnd().TrimEnd(']') + ", null]");

        Assert.DoesNotThrow(_cache.Load);
        Assert.That(_cache.For("gb"), Is.Null);
        Assert.That(_cache.For(ExternalTitleDbTestHandler.SystemId), Is.Null);
    }

    [TestCase("alias", TestName = "ManifestRow_DisablesTitleDb wrong-case alias")]
    [TestCase("noVersion", TestName = "ManifestRow_DisablesTitleDb missing version")]
    [TestCase("badVersion", TestName = "ManifestRow_DisablesTitleDb invalid date version")]
    [TestCase("noTime", TestName = "ManifestRow_DisablesTitleDb missing download time")]
    [TestCase("duplicate", TestName = "ManifestRow_DisablesTitleDb duplicate path")]
    [TestCase("datName", TestName = "ManifestRow_DisablesTitleDb mismatched DatName")]
    public void ManifestRow_DisablesTitleDb(string problem)
    {
        Write();
        var alias = Compact[..(Compact.LastIndexOf('/') + 1)] + Path.GetFileName(Compact).ToUpperInvariant();
        if (problem == "alias")
        {
            // Both the file and its row use another case.
            File.Move(Path.Combine(_cache.Directory, Compact), Path.Combine(_cache.Directory, alias + ".tmp"));
            File.Move(Path.Combine(_cache.Directory, alias + ".tmp"), Path.Combine(_cache.Directory, alias));
        }
        EditManifest(entries =>
        {
            var row = Titledb(entries);
            entries.Remove(row);
            entries.AddRange(problem switch
            {
                "alias" => [row with { File = alias }],
                "noVersion" => [row with { Version = null }],
                "badVersion" => [row with { Version = "2026-01-01" }],
                "noTime" => [row with { DownloadedAt = default }],
                "duplicate" => [row, row],
                _ => [row with { DatName = "Nintendo - Nintendo Switch" }],
            });
            return entries;
        });

        AssertOnlyTitleDbDisabled();
    }

    private const string ValidRow =
        """{"titleId":"0100000000001000","name":"Game","publisher":null,"developer":null,"releaseYear":null,"genres":[],"languages":[],"boxArtUrl":null,"snapUrl":null}""";

    [TestCase("[{\"titleId\":\"0100000000001000\"}]", TestName = "CompactFile_DisablesTitleDb malformed row")]
    [TestCase("LANG:null", TestName = "CompactFile_DisablesTitleDb null languages")]
    [TestCase("LANG:[null]", TestName = "CompactFile_DisablesTitleDb null language member")]
    [TestCase("[" + ValidRow + "," + ValidRow + "]", TestName = "CompactFile_DisablesTitleDb duplicate id")]
    [TestCase("not json", TestName = "CompactFile_DisablesTitleDb unparsable")]
    public void CompactFile_DisablesTitleDb(string json)
    {
        Write();
        RewriteCompact(json.StartsWith("LANG:") ? $"[{ValidRow.Replace("\"languages\":[]", $"\"languages\":{json[5..]}")}]" : json);

        AssertOnlyTitleDbDisabled();
    }

    [Test]
    public void Sha1Mismatch_DisablesTitleDb()
    {
        Write();
        RewriteCompact($"[{ValidRow}]", fixSha1: false);

        AssertOnlyTitleDbDisabled();
    }

    private string CompactPath => Path.Combine(_cache.Directory, Compact);

    [Test]
    public void LinkedTitleDbDirectory_DisablesTitleDb()
    {
        Write();
        var folder = Path.GetDirectoryName(CompactPath)!;
        var outside = Path.Combine(_dir.Path, "outside");
        Directory.Move(folder, outside);
        if (!TestLinks.TryLinkDirectory(folder, outside))
            Assert.Ignore("this account can't create a directory link");
        try
        {
            AssertOnlyTitleDbDisabled();
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(folder);
        }
    }

    [TestCase("link", TestName = "UnsafeCompactLeaf_DisablesTitleDb linked")]
    [TestCase("fifo", TestName = "UnsafeCompactLeaf_DisablesTitleDb fifo")]
    [TestCase("directory", TestName = "UnsafeCompactLeaf_DisablesTitleDb directory")]
    public void UnsafeCompactLeaf_DisablesTitleDb(string problem)
    {
        Write();
        var outside = Path.Combine(_dir.Path, "outside.json");
        File.Move(CompactPath, outside);
        var made = problem switch
        {
            "link" => TestLinks.TryLinkFile(CompactPath, outside),
            "fifo" => TestLinks.TryCreateFifo(CompactPath),
            _ => Directory.CreateDirectory(CompactPath).Exists,
        };
        if (!made)
            Assert.Ignore($"this OS or account can't make a {problem}");

        AssertOnlyTitleDbDisabled();
    }

    [Test]
    public void CompactReplacedWhileRead_DisablesTitleDb()
    {
        Write();
        var bytes = File.ReadAllBytes(CompactPath);
        var replaced = false;
        _cache.AfterCompactRead = path =>
        {
            File.WriteAllBytes(path + ".new", bytes); // the same bytes: only the file's identity tells it apart
            File.Move(path, path + ".old"); // allowed on the open file: it was opened with delete sharing
            File.Move(path + ".new", path);
            replaced = true;
        };

        AssertOnlyTitleDbDisabled();
        Assert.That(replaced, Is.True, "the swap itself went through");
    }

    [Test]
    public void OversizedCompact_DisablesTitleDb()
    {
        Write();
        _cache.MaxCompactBytes = new FileInfo(CompactPath).Length - 1;

        AssertOnlyTitleDbDisabled();

        _cache.MaxCompactBytes = new FileInfo(CompactPath).Length;
        _cache.Load();
        Assert.That(_cache.For(ExternalTitleDbTestHandler.SystemId), Is.Not.Null, "at the cap it loads");
    }
}
