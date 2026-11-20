using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>B1, B2, B8: the <c>saves.json</c> model, its reader, writer and field rules.</summary>
public class GameSavesTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static GameSave Save(string? id = null, string extension = ".state", long size = 3) => new()
    {
        Id = id ?? GameSaves.NewId(),
        Name = "Before boss",
        Description = "",
        Kind = GameSaves.KindState,
        Release = "Tetris (World) (Rev 1).gb",
        Extension = extension,
        SizeBytes = size,
        Sha1 = new string('a', 40),
        UploadedBy = "alice",
        UploadedAt = When,
        UpdatedBy = null,
        UpdatedAt = When,
    };

    /// <summary>Every listed entry exists with its size.</summary>
    private static long? AllPresent(string entry) => 3;

    private static SavesReadResult Parse(GameSave save) => GameSaves.Parse(GameSaves.Serialize([save])!, AllPresent);

    [Test]
    public void RoundTrip_KeepsEveryField_AndTheEntryName()
    {
        var saves = new[] { Save(), Save(extension: "") };

        var result = GameSaves.Parse(GameSaves.Serialize(saves)!, AllPresent);

        Assert.That(result.State, Is.EqualTo(SavesState.Valid), result.Problem);
        Assert.That(result.Index!.Version, Is.EqualTo(2));
        Assert.That(result.Index.Saves, Is.EqualTo(saves));
        Assert.That(result.Index.Saves[0].Entry, Is.EqualTo($"saves/alice/{saves[0].Id}.state"));
        Assert.That(result.Index.Saves[1].Entry, Is.EqualTo($"saves/alice/{saves[1].Id}"));
    }

    [Test]
    public void Serialize_WritesTheB2Shape()
    {
        var json = JsonNode.Parse(GameSaves.Serialize([Save("0123456789abcdef0123456789abcdef")])!)!;

        Assert.That(json["version"]!.GetValue<int>(), Is.EqualTo(2));
        var save = json["saves"]![0]!.AsObject();
        Assert.That(save.Select(p => p.Key), Is.EqualTo(new[]
        {
            "id", "name", "description", "kind", "release", "extension", "sizeBytes", "sha1", "uploadedBy", "uploadedAt", "updatedBy", "updatedAt",
        }));
        Assert.That(save["updatedBy"], Is.Null);
    }

    /// <summary>Release editing B16, B17: a version 1 index is still read, its records legacy with entries directly in saves/.</summary>
    [Test]
    public void Parse_Version1_MarksEveryRecordLegacy_WithTheFlatEntry()
    {
        var save = Save() with { UploadedBy = null };
        var v1 = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(GameSaves.Serialize([save with { UploadedBy = "alice" }])!)
            .Replace("\"version\": 2", "\"version\": 1").Replace("\"uploadedBy\": \"alice\"", "\"uploadedBy\": null"));

        var result = GameSaves.Parse(v1, AllPresent);

        Assert.That(result.State, Is.EqualTo(SavesState.Valid), result.Problem);
        Assert.That(result.Index!.Saves.Single().Legacy, Is.True);
        Assert.That(result.Index.Saves.Single().Entry, Is.EqualTo($"saves/{save.Id}.state"));
        Assert.That(Parse(Save()).Index!.Saves.Single().Legacy, Is.False, "version 2 records aren't legacy");
    }

    [TestCase(null)]
    [TestCase("a/b")]
    [TestCase("..")]
    public void Version2_NeedsAnOwnerThatCanNameAFolder(string? owner)
    {
        Assert.That(() => GameSaves.Serialize([Save() with { UploadedBy = owner }]), Throws.ArgumentException);
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(GameSaves.Serialize([Save()])!)
            .Replace("\"uploadedBy\": \"alice\"", owner is null ? "\"uploadedBy\": null" : $"\"uploadedBy\": \"{owner}\""));
        Assert.That(GameSaves.Parse(bytes, AllPresent).State, Is.EqualTo(SavesState.Unreadable));
    }

    [Test]
    public void Serialize_RefusesALegacyRecord() =>
        Assert.That(() => GameSaves.Serialize([Save() with { Legacy = true }]), Throws.ArgumentException);

    [Test]
    public void Entry_UsesTheOwnerLowercased() =>
        Assert.That(Save("0123456789abcdef0123456789abcdef") with { UploadedBy = "Alice.B" } is var s ? s.Entry : null,
            Is.EqualTo("saves/alice.b/0123456789abcdef0123456789abcdef.state"));

    /// <summary>B18: legacy records become version 2 ones; bytes are re-keyed when already added, else read; owners kept or admin.</summary>
    [Test]
    public void Upgrade_ReKeysOrReadsLegacyBytes_AndFixesOwners()
    {
        var kept = Save() with { Legacy = true };
        var replaced = Save() with { Legacy = true, UploadedBy = null };
        var bad = Save() with { Legacy = true, UploadedBy = "x" };
        var current = Save() with { UploadedBy = "bob" };
        Func<Stream> newBytes = () => new MemoryStream([9]);
        var added = new Dictionary<string, Func<Stream>> { [replaced.Entry] = newBytes };
        var removed = new HashSet<string>();
        var read = new List<string>();

        var result = GameSaves.Upgrade([kept, replaced, bad, current], s => { read.Add(s.Id); return () => new MemoryStream([1]); }, added, removed);

        Assert.That(result.Select(s => (s.Legacy, s.UploadedBy)),
            Is.EqualTo(new[] { (false, (string?)"alice"), (false, "admin"), (false, "admin"), (false, "bob") }));
        Assert.That(result[1].UpdatedBy, Is.EqualTo(replaced.UpdatedBy), "updatedBy is left as it is");
        Assert.That(read, Is.EqualTo(new[] { kept.Id, bad.Id }), "a replaced file's new bytes aren't read from the ZIP");
        Assert.That(ReferenceEquals(added[$"saves/admin/{replaced.Id}.state"], newBytes), Is.True, "re-keyed");
        Assert.That(added.Keys, Is.EquivalentTo(new[] { $"saves/alice/{kept.Id}.state", $"saves/admin/{replaced.Id}.state", $"saves/admin/{bad.Id}.state" }));
        Assert.That(removed, Is.EquivalentTo(new[] { kept.Entry, replaced.Entry, bad.Entry }));
        Assert.That(GameSaves.Serialize(result), Is.Not.Null);
    }

    [Test]
    public void Serialize_OverOneMiB_IsNull()
    {
        var big = Enumerable.Range(0, 1200).Select(_ => Save() with { Description = new string('d', 1000) }).ToList();

        Assert.That(GameSaves.Serialize(big), Is.Null);
        Assert.That(GameSaves.Serialize(big.Take(10).ToList()), Is.Not.Null);
    }

    [Test]
    public void Parse_OverOneMiB_IsUnreadable() =>
        Assert.That(GameSaves.Parse(new byte[GameSaves.MaxIndexBytes + 1], AllPresent).State, Is.EqualTo(SavesState.Unreadable));

    private static IEnumerable<TestCaseData> Invalid()
    {
        static TestCaseData Case(string name, GameSave save) => new TestCaseData(save).SetName("Invalid " + name);
        yield return Case("uppercase id", Save("0123456789ABCDEF0123456789ABCDEF"));
        yield return Case("short id", Save("0123"));
        yield return Case("unknown kind", Save() with { Kind = "snapshot" });
        yield return Case("extension without a dot", Save(extension: "state"));
        yield return Case("extension too long", Save(extension: ".abcdefghi"));
        yield return Case("extension upper case", Save(extension: ".SRM"));
        yield return Case("untrimmed name", Save() with { Name = " x" });
        yield return Case("empty name", Save() with { Name = "" });
        yield return Case("control character in the name", Save() with { Name = "a\tb" });
        yield return Case("description too long", Save() with { Description = new string('d', 1001) });
        yield return Case("tab in the description", Save() with { Description = "a\tb" });
    }

    [TestCaseSource(nameof(Invalid))]
    public void Parse_InvalidRecord_IsUnreadable(GameSave save) =>
        Assert.That(Parse(save).State, Is.EqualTo(SavesState.Unreadable));

    [Test]
    public void Parse_DuplicateId_WrongVersion_UnknownJson_AreUnreadable()
    {
        var save = Save();
        var duplicate = GameSaves.Serialize([save, save with { Name = "Other" }])!;
        var version = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(GameSaves.Serialize([save])!).Replace("\"version\": 2", "\"version\": 3"));

        Assert.That(GameSaves.Parse(duplicate, AllPresent).State, Is.EqualTo(SavesState.Unreadable));
        Assert.That(GameSaves.Parse(version, AllPresent).Problem, Is.EqualTo("saves.json version 3 is not 1 or 2"));
        Assert.That(GameSaves.Parse("{"u8.ToArray(), AllPresent).State, Is.EqualTo(SavesState.Unreadable));
        Assert.That(GameSaves.Parse("""{"version":1}"""u8.ToArray(), AllPresent).State, Is.EqualTo(SavesState.Unreadable));
    }

    [Test]
    public void Parse_ListedEntryMissingOrOfAnotherLength_IsUnreadable()
    {
        var bytes = GameSaves.Serialize([Save()])!;

        Assert.That(GameSaves.Parse(bytes, _ => null).State, Is.EqualTo(SavesState.Unreadable));
        Assert.That(GameSaves.Parse(bytes, _ => 4).State, Is.EqualTo(SavesState.Unreadable));
    }

    [TestCase("save.state", ".state")]
    [TestCase("x.tar.gz", ".gz")]
    [TestCase(".state", ".state")]
    [TestCase("GAME.SRM", ".srm")]
    [TestCase("x.", "")]
    [TestCase("x.TOOLONGEXT", "")]
    [TestCase("x.12345678", ".12345678")]
    [TestCase("x.st@te", "")]
    [TestCase("noextension", "")]
    [TestCase(null, "")]
    public void ExtensionOf_FollowsB1(string? fileName, string expected) =>
        Assert.That(GameSaves.ExtensionOf(fileName), Is.EqualTo(expected));

    [TestCase("  Before boss  ", null, "Before boss")]
    [TestCase("", "1–100", "")]
    [TestCase("   ", "1–100", "")]
    [TestCase("a\u0007", "control", "a\u0007")]
    public void NameProblem_TrimsAndChecks(string name, string? problem, string trimmed)
    {
        var error = GameSaves.NameProblem(name, out var actual);

        Assert.That(error, problem is null ? Is.Null : Does.Contain(problem));
        Assert.That(actual, Is.EqualTo(trimmed));
    }

    [Test]
    public void NameProblem_Over100_AndDescriptionRules()
    {
        Assert.That(GameSaves.NameProblem(new string('n', 100), out _), Is.Null);
        Assert.That(GameSaves.NameProblem(new string('n', 101), out _), Is.Not.Null);
        Assert.That(GameSaves.DescriptionProblem(null), Is.Null);
        Assert.That(GameSaves.DescriptionProblem("line\nline"), Is.Null);
        Assert.That(GameSaves.DescriptionProblem(new string('d', 1000)), Is.Null);
        Assert.That(GameSaves.DescriptionProblem(new string('d', 1001)), Is.Not.Null);
        Assert.That(GameSaves.DescriptionProblem("a\r\nb"), Is.Not.Null);
    }

    [Test]
    public void Names_IgnoresCase() =>
        Assert.That(GameSaves.Names(Save(), "TETRIS (WORLD) (REV 1).GB"), Is.True);

    private static LibraryZip Zip(params (string Name, byte[] Bytes)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }
        stream.Position = 0;
        var systems = Substitute.For<ISystemRegistry>();
        systems.All.Returns(Array.Empty<ISystemHandler>());
        return LibraryZip.Open(stream, "test.zip", new InfoJsonReader([], systems), null);
    }

    [Test]
    public void Read_FromAZip_AbsentValidAndUnreadable()
    {
        var save = Save();
        using (var none = Zip(("info.json", "{}"u8.ToArray())))
            Assert.That(GameSaves.Read(none).State, Is.EqualTo(SavesState.Absent));
        using (var valid = Zip((LibraryZip.SavesIndexEntry, GameSaves.Serialize([save])!), (save.Entry, [1, 2, 3])))
            Assert.That(GameSaves.Read(valid).Index!.Saves.Single().Id, Is.EqualTo(save.Id));
        using (var noIndex = Zip((save.Entry, [1, 2, 3])))
            Assert.That(GameSaves.Read(noIndex).State, Is.EqualTo(SavesState.Unreadable));
        using var tooBig = Zip((LibraryZip.SavesIndexEntry, new byte[GameSaves.MaxIndexBytes + 1]));
        Assert.That(GameSaves.Read(tooBig).State, Is.EqualTo(SavesState.Unreadable));
    }

    [TestCase("alice", "alice", true)]
    [TestCase("Alice", "ALICE", true)]
    [TestCase("alice", "bob", false)]
    [TestCase(null, "alice", false)]
    [TestCase("alice", null, false)]
    [TestCase(null, null, false)]
    public void IsOwner_MatchesUploadedByIgnoringCase_NullOwnsNothing(string? uploadedBy, string? username, bool owner)
    {
        var save = Save() with { UploadedBy = uploadedBy };

        Assert.That(GameSaves.IsOwner(save, username), Is.EqualTo(owner));
        Assert.That(new SaveViewer(username, IsAdmin: false).CanSee(save), Is.EqualTo(owner));
        Assert.That(new SaveViewer(username, IsAdmin: true).CanSee(save), Is.True);
    }
}
