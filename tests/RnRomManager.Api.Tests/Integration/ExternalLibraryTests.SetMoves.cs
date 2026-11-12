using System.Security.Cryptography;
using System.Text.Json;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B18: move-release and merge of releases on a FileSets system keep every file's exact name.</summary>
public partial class ExternalLibraryTests
{
    private const string Disc2Cue = "Set Game (USA) (Disc 2).cue";
    private const string Disc2Track1 = "Set Game (USA) (Disc 2) (Track 1).bin";
    private const string Disc2Track2 = "Set Game (USA) (Disc 2) (Track 2).bin";

    /// <summary>"Set Game" with disc 1 (from <see cref="SetGameAsync"/>) and disc 2, both sets, in S/.</summary>
    private async Task<Guid> TwoDiscGameAsync()
    {
        var gameId = await SetGameAsync();
        DropDisc(Disc2Cue, (Disc2Track1, SetData2), (Disc2Track2, Audio2));
        await RunJobAsync(Pipeline());
        Assert.That(SetItems().Select(i => i.Outcome), Is.All.EqualTo(IngestOutcome.Ingested));
        _items.Clear();
        return gameId;
    }

    private Task<LibraryEditResult> MoveToNewGameAsync(Guid gameId, long romId, string title, LibraryEditor? editor = null) =>
        (editor ?? EditorWith()).MoveReleaseAsync(gameId, romId, null, title, Editor, default);

    [Test]
    public async Task SetMove_IntoAFolderWhereANameIsTaken_IsNameTaken_AndNothingMoves()
    {
        var gameId = await TwoDiscGameAsync();
        Directory.CreateDirectory(SetFolder('Z'));
        File.WriteAllBytes(Path.Combine(SetFolder('Z'), Disc2Track2), [1]);

        var result = await MoveToNewGameAsync(gameId, await RomIdAsync(SetData2), "Zeta Game");

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.NameTaken));
        Assert.That(FilesIn('S'), Is.EqualTo(With(Disc2Cue, Disc2Track1, Disc2Track2, "Set Game.zip")));
        Assert.That(FilesIn('Z'), Is.EqualTo(new[] { Disc2Track2 }));
    }

    [Test]
    public async Task SetMove_WritesAV2Record_AndMovesEveryFileUnderItsName()
    {
        var gameId = await TwoDiscGameAsync();
        string? record = null;
        var editor = EditorWith(new ExternalEditHooks
        {
            Backup = new ExternalZipBackupHooks { AfterPromotion = path => { if (path.EndsWith(ExternalRomName.RecordSuffix, StringComparison.Ordinal)) record = File.ReadAllText(path); } },
        });

        var result = await MoveToNewGameAsync(gameId, await RomIdAsync(SetData2), "Zeta Game", editor);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(FilesIn('Z'), Is.EqualTo(new[] { Disc2Track1, Disc2Track2, Disc2Cue, "Zeta Game.zip" }.Order(StringComparer.Ordinal)));
        Assert.That(FilesIn('S'), Is.EqualTo(With("Set Game.zip")));
        var json = JsonDocument.Parse(record!).RootElement;
        Assert.That(json.GetProperty("version").GetInt32(), Is.EqualTo(MoveRecord.CurrentVersion));
        Assert.That(json.GetProperty("files").EnumerateArray().Select(f => (f.GetProperty("sourceExternalFileName").GetString(), f.GetProperty("targetExternalFileName").GetString())),
            Is.EqualTo(new[] { (Disc2Cue, Disc2Cue), (Disc2Track2, Disc2Track2) }));
    }

    [Test]
    public async Task SetMove_SecondFileFailsToMove_UndoesTheFirst_AndIsIoError()
    {
        var gameId = await TwoDiscGameAsync();
        var editor = EditorWith(new ExternalEditHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => Path.GetFileName(to).StartsWith(Disc2Cue, StringComparison.Ordinal) && to.Contains(Path.DirectorySeparatorChar + "Z" + Path.DirectorySeparatorChar)
                    ? new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused")
                    : NoReplaceRename.Move(from, to),
            },
        });

        var result = await MoveToNewGameAsync(gameId, await RomIdAsync(SetData2), "Zeta Game", editor);

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        Assert.That(FilesIn('S'), Is.EqualTo(With(Disc2Cue, Disc2Track1, Disc2Track2, "Set Game.zip")));
        Assert.That(File.ReadAllBytes(Path.Combine(SetFolder('S'), Disc2Track1)), Is.EqualTo(SetData2));
        Assert.That(FilesIn('Z'), Is.Empty);
    }

    [Test]
    public async Task SetMove_OfASingleIso_ToAGameInAnotherFolder_KeepsItsExactName()
    {
        await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso));
        var setGame = await SetGameAsync();
        var discGame = (await Service<IGameRepository>().ListAsync(new GameQuery(), default)).Items.Single(g => g.DisplayTitle == "Disc Game").Id;

        var result = await EditorWith().MoveReleaseAsync(discGame, await RomIdAsync(DiscIso), setGame, null, Editor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(FilesIn('S'), Is.EqualTo(With("Disc Game (USA).iso", "Set Game.zip")));
        Assert.That(Read(Path.Combine(SetFolder('S'), "Set Game.zip")).Roms.Select(r => r.ExternalFileName), Does.Contain("Disc Game (USA).iso"));
    }

    [Test]
    public async Task SetMerge_InTheSameFolder_MovesNoFiles()
    {
        var sun = RandomNumberGenerator.GetBytes(2352 * 2 + 12);
        WriteDats(new FixtureGame("Sun Game (USA)", "Sun Game (USA) (Track 1).bin", sun));
        var setGame = await SetGameAsync();
        DropDisc("Sun Game (USA).cue", ("Sun Game (USA) (Track 1).bin", sun), ("Sun Game (USA) (Track 2).bin", Audio2));
        await RunJobAsync(Pipeline());
        var sunGame = _items.First(i => i.GameId is not null).GameId!.Value;
        var before = FilesIn('S');
        var editor = EditorWith(new ExternalEditHooks { Move = new ExternalMoveHooks { Rename = (_, _) => throw new AssertionException("nothing may move") } });

        var result = await editor.MergeAsync(sunGame, setGame, Editor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(FilesIn('S'), Is.EqualTo(before.Where(f => f != "Sun Game.zip")));
        Assert.That(Read(Path.Combine(SetFolder('S'), "Set Game.zip")).ExternalFileNames(), Has.Member("Sun Game (USA).cue"));
    }

    [Test]
    public async Task SetMerge_LogicalNameClash_IsNameTaken()
    {
        // Disc Game's iso moves to Zeta (Z/); Dark Game then stores another "Disc Game (USA).iso" in D/.
        await IngestAsync(Pipeline(), DropSet("disc.iso", DiscIso));
        var discGame = (await Service<IGameRepository>().ListAsync(new GameQuery(), default)).Items.Single().Id;
        var moved = await MoveToNewGameAsync(discGame, await RomIdAsync(DiscIso), "Zeta Game");
        Assert.That(moved.Code, Is.Null, moved.Details);
        Assert.That(Code(await IngestAsync(Pipeline(), DropSet("dark.iso", DarkIso))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var dark = (await Service<IGameRepository>().ListAsync(new GameQuery(), default)).Items.Single(g => g.DisplayTitle == "Dark Game").Id;

        var result = await EditorWith().MergeAsync(dark, moved.GameId!.Value, Editor, default);

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.NameTaken));
        Assert.That(FilesIn('D'), Is.EqualTo(new[] { "Dark Game.zip", "Disc Game (USA).iso" }));
    }
}
