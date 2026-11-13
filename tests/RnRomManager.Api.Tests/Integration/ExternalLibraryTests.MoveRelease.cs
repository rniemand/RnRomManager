using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec W3: moving an external release between games, with its file, backup and recovery record.</summary>
public partial class ExternalLibraryTests
{
    private string MysteryFolder => Path.Combine(_factory.Root, "roms", "ext", "M");
    private string MovedUsa => Path.Combine(MysteryFolder, $"Mystery Game.{GameName}.xci");
    private string MoveBackup => Path.Combine(Bucket, "Space Game.zip" + ExternalRomName.MoveBackupSuffix);
    private string MoveRecordPath => MoveBackup + ExternalRomName.RecordSuffix;
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    /// <summary>"Mystery Game" (one release, resolved from Review) beside "Space Game" (two releases).</summary>
    private async Task<(Guid Space, Guid Mystery)> TwoGamesAsync()
    {
        var space = await TwoReleaseGameAsync();
        var (id, _) = await QueueAsync("mystery.xci");
        var mystery = (await Resolver().ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default)).GameId!.Value;
        return (space, mystery);
    }

    private async Task<long> RomIdAsync(byte[] bytes)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QuerySingleAsync<long>("SELECT id FROM roms WHERE sha1 = @sha", new { sha = Sha(bytes) });
    }

    private async Task<LibraryEditResult> MoveAsync(Guid from, byte[] release, Guid? to, string? newTitle = null, LibraryEditor? editor = null) =>
        await (editor ?? EditorWith()).MoveReleaseAsync(from, await RomIdAsync(release), to, newTitle, Editor, default);

    private static string[] Listed(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName)];
    }

    private void AssertNotMoved(byte[] space, byte[] mystery)
    {
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom), "the file is back at its source name");
        Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(space), "the source ZIP is as it was");
        Assert.That(File.ReadAllBytes(MysteryZip), Is.EqualTo(mystery), "the target ZIP is as it was");
        Assert.That(File.Exists(MovedUsa), Is.False);
    }

    [Test]
    public async Task ToAnExistingGame_TheFileMovesBesideTheTarget_AndTheBackupIsCleanedUp()
    {
        var (space, mystery) = await TwoGamesAsync();

        var result = await MoveAsync(space, Rom, mystery);

        Assert.That((result.Code, result.GameId), Is.EqualTo(((string?)null, (Guid?)mystery)));
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom));
        Assert.That(File.Exists(Sidecar), Is.False);
        Assert.That(Read(ZipPath).Roms.Select(r => r.ExternalFileName), Is.EqualTo(new[] { Path.GetFileName(EuropeSidecar) }));
        Assert.That(Read(MysteryZip).Roms.Select(r => r.ExternalFileName), Does.Contain(Path.GetFileName(MovedUsa)));
        Assert.That(Listed(MysteryZip), Has.None.StartsWith(LibraryZip.RomsFolder), "no ROM entry was passed to the writer");
        Assert.That(File.Exists(MoveBackup) || File.Exists(MoveRecordPath), Is.False);
    }

    [Test]
    public async Task LastRelease_ToAnExistingGame_DeletesTheSourceZip()
    {
        var (space, mystery) = await TwoGamesAsync();

        var result = await MoveAsync(mystery, Mystery, space);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(MysteryZip), Is.False);
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, "Space Game.Mystery Game.xci")), Is.EqualTo(Mystery));
    }

    [Test]
    public async Task ToANewGame_TheFileMovesBesideItsNewZip()
    {
        var (space, _) = await TwoGamesAsync();

        var result = await MoveAsync(space, Rom, null, "Nova Game");

        Assert.That(result.Code, Is.Null);
        Assert.That(File.ReadAllBytes(Path.Combine(_factory.Root, "roms", "ext", "N", $"Nova Game.{GameName}.xci")), Is.EqualTo(Rom));
        Assert.That(Read(Path.Combine(_factory.Root, "roms", "ext", "N", "Nova Game.zip")).GameId, Is.EqualTo(result.GameId));
    }

    [Test]
    public async Task MissingFile_OnlyItsPointerMoves()
    {
        var (space, mystery) = await TwoGamesAsync();
        File.Delete(Sidecar);

        var result = await MoveAsync(space, Rom, mystery);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(MovedUsa), Is.False);
        Assert.That(Read(MysteryZip).Roms.Select(r => r.ExternalFileName), Does.Contain(Path.GetFileName(MovedUsa)));
    }

    [Test]
    public async Task FailedTargetWrite_LeavesTheSourceAsItWas()
    {
        var (space, mystery) = await TwoGamesAsync();
        var (spaceZip, mysteryZip) = (File.ReadAllBytes(ZipPath), File.ReadAllBytes(MysteryZip));

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(new ExternalEditHooks { Writer = FailVerify(zipPath: MysteryZip) }));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        AssertNotMoved(spaceZip, mysteryZip);
        Assert.That(File.Exists(MoveBackup) || File.Exists(MoveRecordPath), Is.False);
    }

    [TestCase(false, TestName = "FailedSourceRebuild_UndoesTheTarget_AndMovesTheFileBack returned failure")]
    [TestCase(true, TestName = "FailedSourceRebuild_UndoesTheTarget_AndMovesTheFileBack thrown failure")]
    public async Task FailedSourceRebuild_UndoesTheTarget_AndMovesTheFileBack(bool thrown)
    {
        var (space, mystery) = await TwoGamesAsync();
        var spaceZip = File.ReadAllBytes(ZipPath);
        var hooks = thrown
            ? new ExternalEditHooks { Writer = new LibraryZipWriterHooks { AfterTempClosed = () => { if (File.Exists(ZipPath + ".tmp")) throw new InvalidOperationException("boom"); } } }
            : new ExternalEditHooks { Writer = FailVerify() };

        if (thrown)
            Assert.That(() => MoveAsync(space, Rom, mystery, editor: EditorWith(hooks)), Throws.InvalidOperationException);
        else
            Assert.That((await MoveAsync(space, Rom, mystery, editor: EditorWith(hooks))).Code, Is.EqualTo(LibraryEditResult.VerifyFailed));

        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(spaceZip));
        Assert.That(Read(MysteryZip).Roms.Select(r => r.Metadata.Sha1), Is.EqualTo(new[] { Sha(Mystery) }), "the target is back to its own release");
        Assert.That(File.Exists(MoveBackup) || File.Exists(MoveRecordPath), Is.False, "a fully rolled-back move removes its backup");
    }

    [Test]
    public async Task DbFailure_KeepsTheNewNames()
    {
        var (space, mystery) = await TwoGamesAsync();
        var mirror = Substitute.For<ILibraryMirror>();
        mirror.ApplyAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(mirror: mirror));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom));
        Assert.That(Read(ZipPath).Roms, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LastRelease_TargetReplacedBeforeTheMirror_KeepsTheSourceZip()
    {
        var (space, mystery) = await TwoGamesAsync();
        var hooks = new ExternalEditHooks
        {
            BeforeMirror = () =>
            {
                File.Copy(ZipPath, ZipPath + ".copy");
                File.Delete(ZipPath);
                File.Move(ZipPath + ".copy", ZipPath);
            },
        };

        var result = await MoveAsync(mystery, Mystery, space, editor: EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        Assert.That(File.Exists(MysteryZip), Is.True);
    }

    [Test]
    public async Task TargetLostBeforeTheSourceRebuild_KeepsTheOriginalSourceZip()
    {
        var (space, mystery) = await TwoGamesAsync();
        var spaceZip = File.ReadAllBytes(ZipPath);

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(new ExternalEditHooks { BeforeSourceRebuild = () => File.Delete(MysteryZip) }));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(spaceZip));
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom));
        Assert.That(File.Exists(MoveBackup) && File.Exists(MoveRecordPath), Is.True);
    }

    [Test]
    public async Task TargetLostAfterTheSourceRebuild_KeepsTheBackupWithTheReleaseAndNote_AndItsRecord()
    {
        var (space, mystery) = await TwoGamesAsync();
        Assert.That((await EditorWith().SaveNoteAsync(space, await RomIdAsync(Rom), "my note", null, default)).Code, Is.Null);

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(new ExternalEditHooks { BeforeMirror = () => File.Delete(MysteryZip) }));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        var record = await ExternalMoveRecord.ReadAsync(Path.Combine(_factory.Root, "roms"), MoveBackup, Service<InfoJsonReader>());
        Assert.That((record.SourceGameId, record.TargetGameId, record.RomSha1), Is.EqualTo((space, mystery, Sha(Rom))));
        Assert.That(Listed(MoveBackup), Does.Contain(LibraryZip.NoteEntry($"{GameName}.xci")));
    }

    [Test]
    public async Task FailedRecordPublication_LeavesTheFileAndBothZips()
    {
        var (space, mystery) = await TwoGamesAsync();
        var (spaceZip, mysteryZip) = (File.ReadAllBytes(ZipPath), File.ReadAllBytes(MysteryZip));
        var hooks = new ExternalEditHooks
        {
            Backup = new ExternalZipBackupHooks
            {
                BeforePromotion = partial =>
                {
                    if (partial.EndsWith(ExternalRomName.RecordSuffix + ExternalRomName.PartialSuffix, StringComparison.Ordinal))
                        File.WriteAllText(MoveRecordPath, "an occupant");
                },
            },
        };

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        AssertNotMoved(spaceZip, mysteryZip);
        Assert.That(File.ReadAllText(MoveRecordPath), Is.EqualTo("an occupant"));
        Assert.That(File.Exists(MoveBackup), Is.False, "the move's own backup is removed");
    }

    [Test]
    public async Task SourceReplacedBeforeTheRebuild_TheTargetIsUndone_AndTheReplacementAndBackupStay()
    {
        var (space, mystery) = await TwoGamesAsync();
        byte[]? replacement = null;
        var hooks = new ExternalEditHooks
        {
            BeforeSourceRebuild = () =>
            {
                File.Copy(ZipPath, ZipPath + ".copy");
                File.Delete(ZipPath);
                File.Move(ZipPath + ".copy", ZipPath);
                replacement = File.ReadAllBytes(ZipPath);
            },
        };

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(replacement));
        Assert.That(Read(MysteryZip).Roms, Has.Count.EqualTo(1), "the target was undone");
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.Exists(MoveBackup), Is.True, "the source isn't the ZIP the move opened, so its backup stays");
    }

    [Test]
    public async Task FailedUndo_WithTheTargetIntact_KeepsTheFileBesideIt()
    {
        var (space, mystery) = await TwoGamesAsync();
        var targetWrites = 0;
        var hooks = new ExternalEditHooks
        {
            Writer = new LibraryZipWriterHooks
            {
                AfterTempClosed = () =>
                {
                    Corrupt(ZipPath); // the source rebuild fails
                    if (File.Exists(MysteryZip + ".tmp") && ++targetWrites == 2)
                        Corrupt(MysteryZip); // and so does the target's undo
                },
            },
        };

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom));
        Assert.That(Read(MysteryZip).Roms, Has.Count.EqualTo(2), "the published target still lists it");
    }

    [Test]
    public async Task ReplacedTarget_IsKept_AndTheFileStaysAtItsNewPath()
    {
        var (space, mystery) = await TwoGamesAsync();
        byte[]? replacement = null;
        var hooks = new ExternalEditHooks
        {
            Writer = new LibraryZipWriterHooks
            {
                AfterTempClosed = () =>
                {
                    if (!Corrupt(ZipPath))
                        return;
                    File.WriteAllText(MysteryZip + ".copy", "a replacement");
                    File.Delete(MysteryZip);
                    File.Move(MysteryZip + ".copy", MysteryZip);
                    replacement = File.ReadAllBytes(MysteryZip);
                },
            },
        };

        var result = await MoveAsync(space, Rom, mystery, editor: EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        Assert.That(File.ReadAllBytes(MysteryZip), Is.EqualTo(replacement));
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom), "unlisted now, but never moved onto unknown ground");
    }

    /// <summary>An external game written straight to disk and mirrored, for names ingest wouldn't produce.</summary>
    private async Task<Guid> CraftedGameAsync(string title, string fileName, string pointer, byte[] bytes, string? note = null)
    {
        var game = TestLibrary.Game(title, TestLibrary.Rom(fileName, bytes) with { ExternalFileName = pointer }) with { System = ExternalTestHandler.SystemId };
        var folder = Path.Combine(_factory.Root, "roms", "ext", Core.Naming.Bucket.For(title + ".zip"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, pointer), bytes);
        var entries = new Dictionary<string, Func<Stream>>();
        if (note is not null)
            entries[LibraryZip.NoteEntry(fileName)] = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(note));
        await new LibraryZipWriter(Service<InfoJsonReader>()).WriteAsync(Path.Combine(folder, title + ".zip"), null, new LibraryZipChanges(game, entries));
        await Service<DbSync>().SyncAllAsync(default);
        return game.GameId;
    }

    [Test]
    public async Task CollidingLongLogicalName_AndItsNote_UseTheAllocatedName()
    {
        var name = new string('a', 251) + ".nsp"; // 255 characters, the logical name limit
        var (alpha, beta) = (RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var from = await CraftedGameAsync("Alpha", name, "Alpha.nsp", alpha, note: "alpha's note");
        var to = await CraftedGameAsync("Beta", name, "Beta.nsp", beta);

        var result = await MoveAsync(from, alpha, to);

        Assert.That(result.Code, Is.Null);
        var expected = new string('a', 247) + " (2).nsp";
        var target = Path.Combine(_factory.Root, "roms", "ext", "B", "Beta.zip");
        Assert.That(Read(target).Roms.Select(r => r.FileName), Is.EquivalentTo(new[] { name, expected }));
        using var zip = ZipFile.OpenRead(target);
        using var note = new StreamReader(zip.GetEntry(LibraryZip.NoteEntry(expected))!.Open());
        Assert.That(note.ReadToEnd(), Is.EqualTo("alpha's note"));
    }
}
