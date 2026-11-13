using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec W2 and W6: a title edit of an external game moves its ZIP and every external file together.</summary>
public partial class ExternalLibraryTests
{
    private static readonly AuditActor Editor = new(null, "editor", null);

    private string StarZip => Path.Combine(Bucket, "Star Game.zip");
    private string StarUsa => Path.Combine(Bucket, $"Star Game.{GameName}.xci");
    private string StarEurope => Path.Combine(Bucket, $"Star Game.{EuropeName}.nsp");

    /// <summary>"Space Game" with two releases: <see cref="Sidecar"/> and <see cref="EuropeSidecar"/>.</summary>
    private async Task<Guid> TwoReleaseGameAsync()
    {
        var gameId = await FirstReleaseAsync();
        Assert.That(Code(await IngestAsync(Pipeline(), Drop("eu.nsp", Europe))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        return gameId;
    }

    private LibraryEditor EditorWith(ExternalEditHooks? hooks = null, ILibraryMirror? mirror = null)
    {
        var editor = mirror is null ? Service<LibraryEditor>() : ActivatorUtilities.CreateInstance<LibraryEditor>(_factory.Services, mirror);
        editor.ExternalHooks = hooks;
        return editor;
    }

    private Task<LibraryEditResult> RetitleAsync(Guid gameId, string title, LibraryEditor? editor = null) =>
        (editor ?? EditorWith()).PatchAsync(gameId, new GamePatch { Title = title }, Editor, default);

    private async Task<string> ZipPathAsync(Guid gameId)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QuerySingleAsync<string>("SELECT zip_path FROM games WHERE id = @id", new { id = gameId.ToString() });
    }

    private InfoJsonGame Read(string zip)
    {
        using var library = LibraryZip.Open(zip, Service<InfoJsonReader>());
        return library.InfoJson!.Game!;
    }

    private void AssertUntouched()
    {
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
        Assert.That(Read(ZipPath).Title, Is.EqualTo("Space Game"));
        Assert.That(File.Exists(StarZip) || File.Exists(StarUsa) || File.Exists(StarEurope), Is.False);
    }

    [Test]
    public async Task TitleEdit_MovesTheZipAndEveryFile()
    {
        var gameId = await TwoReleaseGameAsync();

        var result = await RetitleAsync(gameId, "Star Game");

        Assert.That(result.Code, Is.Null);
        Assert.That(await ZipPathAsync(gameId), Is.EqualTo("ext/S/Star Game.zip"));
        Assert.That(File.Exists(ZipPath), Is.False, "the old ZIP is gone");
        Assert.That(File.ReadAllBytes(StarUsa), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(StarEurope), Is.EqualTo(Europe));
        Assert.That(File.Exists(Sidecar) || File.Exists(EuropeSidecar), Is.False);
        Assert.That(Read(StarZip).Roms.Select(r => r.ExternalFileName), Is.EquivalentTo(new[] { Path.GetFileName(StarUsa), Path.GetFileName(StarEurope) }));
    }

    [Test]
    public async Task CaseOnlyTitleChange_RenamesNothing()
    {
        var gameId = await TwoReleaseGameAsync();

        var result = await RetitleAsync(gameId, "SPACE GAME");

        Assert.That(result.Code, Is.Null);
        Assert.That(await ZipPathAsync(gameId), Is.EqualTo(ZipRelative));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(Read(ZipPath).Title, Is.EqualTo("SPACE GAME"));
    }

    [Test]
    public async Task ChangeOnlyPastTheByteCut_KeepsTheZipAndRenamesNothing()
    {
        var gameId = await TwoReleaseGameAsync();
        var cjk = new string('游', 180); // 540 UTF-8 bytes; the ZIP base keeps the first 82 characters
        Assert.That((await RetitleAsync(gameId, cjk)).Code, Is.Null);
        var zipPath = await ZipPathAsync(gameId);
        var folder = Path.GetDirectoryName(SafePath.Resolve(Path.Combine(_factory.Root, "roms"), zipPath))!;
        var files = Directory.GetFiles(folder).Order().ToArray();

        var result = await RetitleAsync(gameId, cjk[..170] + "戏" + cjk[171..]);

        Assert.That(result.Code, Is.Null);
        Assert.That(await ZipPathAsync(gameId), Is.EqualTo(zipPath));
        Assert.That(Directory.GetFiles(folder).Order(), Is.EqualTo(files));
    }

    [Test]
    public async Task OneMissingFile_OnlyGetsItsNewName()
    {
        var gameId = await TwoReleaseGameAsync();
        File.Delete(EuropeSidecar);

        var result = await RetitleAsync(gameId, "Star Game");

        Assert.That(result.Code, Is.Null);
        Assert.That(File.ReadAllBytes(StarUsa), Is.EqualTo(Rom));
        Assert.That(File.Exists(StarEurope), Is.False);
        Assert.That(Read(StarZip).Roms.Select(r => r.ExternalFileName), Does.Contain(Path.GetFileName(StarEurope)));
    }

    [Test]
    public async Task AllMissing_IntoALinkedBucket_IsAnIoError()
    {
        var gameId = await TwoReleaseGameAsync();
        File.Delete(Sidecar);
        File.Delete(EuropeSidecar);
        var outside = Directory.CreateDirectory(Path.Combine(_factory.Root, "outside")).FullName;
        var linked = Path.Combine(_factory.Root, "roms", "ext", "Z");
        if (Directory.Exists(linked))
            Directory.Delete(linked); // startup created the empty bucket
        if (!TestLinks.TryLinkDirectory(linked, outside))
            Assert.Ignore("this account can't create a directory link");
        try
        {
            var result = await RetitleAsync(gameId, "Zed Game");

            Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
            Assert.That(await ZipPathAsync(gameId), Is.EqualTo(ZipRelative));
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(linked);
        }
    }

    [Test]
    public async Task StaleRowPointingAtAnotherGamesZip_IsTargetZipUnreadable()
    {
        var gameId = await TwoReleaseGameAsync();
        var (otherId, _) = await QueueAsync("mystery.xci");
        Assert.That((await Resolver().ResolveInJobAsync(otherId, ManualMystery, await AdminAsync(), default)).Code, Is.Null);
        await SwapZipPathsAsync(ZipRelative, "ext/M/Mystery Game.zip");

        var result = await RetitleAsync(gameId, "Star Game");

        Assert.That(result.Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.Exists(StarZip), Is.False);
    }

    [Test]
    public async Task ForcedZipWriteFailure_MovesTheFilesBack()
    {
        var gameId = await TwoReleaseGameAsync();

        var result = await RetitleAsync(gameId, "Star Game", EditorWith(new ExternalEditHooks { Writer = FailVerify(zipPath: StarZip) }));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        AssertUntouched();
    }

    [Test]
    public async Task SecondMoveFails_TheFirstIsUndone_AndNoZipIsWritten()
    {
        var gameId = await TwoReleaseGameAsync();
        var hooks = new ExternalEditHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => Path.GetFileName(from) == Path.GetFileName(EuropeSidecar)
                    ? new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused") : NoReplaceRename.Move(from, to),
            },
        };

        var result = await RetitleAsync(gameId, "Star Game", EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        AssertUntouched();
    }

    [Test]
    public async Task FailedUndo_IsLogged_AndKeepsTheOriginalError()
    {
        var gameId = await TwoReleaseGameAsync();
        var writing = false;
        var hooks = new ExternalEditHooks
        {
            Writer = FailVerify(() => writing = true, StarZip),
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => writing ? new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused") : NoReplaceRename.Move(from, to),
            },
        };

        var result = await RetitleAsync(gameId, "Star Game", EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        Assert.That(File.ReadAllBytes(StarUsa), Is.EqualTo(Rom), "the undo couldn't move it; it is where the edit put it");
        Assert.That(Read(ZipPath).Title, Is.EqualTo("Space Game"));
    }

    [Test]
    public async Task WrongLengthFile_FailsVerification_AndIsMovedBack()
    {
        var gameId = await TwoReleaseGameAsync();
        File.WriteAllBytes(Sidecar, Short);

        var result = await RetitleAsync(gameId, "Star Game");

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Short));
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
        Assert.That(File.Exists(StarZip) || File.Exists(StarUsa), Is.False);
    }

    [Test]
    public async Task ReplacementAtTheOldZipPath_IsKept()
    {
        var gameId = await TwoReleaseGameAsync();
        var hooks = new ExternalEditHooks
        {
            Writer = new LibraryZipWriterHooks
            {
                BeforePublicationAttempt = _ =>
                {
                    File.Copy(ZipPath, ZipPath + ".copy");
                    File.Delete(ZipPath);
                    File.Move(ZipPath + ".copy", ZipPath);
                },
            },
        };

        var result = await RetitleAsync(gameId, "Star Game", EditorWith(hooks));

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(ZipPath), Is.True, "not the ZIP the edit opened, so it is kept");
        Assert.That(File.Exists(StarZip), Is.True);
    }

    [Test]
    public async Task DbFailure_KeepsTheNewNames_AndTheOldZip()
    {
        var gameId = await TwoReleaseGameAsync();
        var mirror = Substitute.For<ILibraryMirror>();
        mirror.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        var result = await RetitleAsync(gameId, "Star Game", EditorWith(mirror: mirror));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        Assert.That(File.ReadAllBytes(StarUsa), Is.EqualTo(Rom));
        Assert.That(File.Exists(StarZip), Is.True);
        Assert.That(File.Exists(ZipPath), Is.True, "the old ZIP stays until the DB has the new path");
    }

    [Test]
    public async Task BusyLock_ChangesNothing()
    {
        var gameId = await TwoReleaseGameAsync();
        using var held = Service<JobLock>().TryAcquire();

        Assert.That((await RetitleAsync(gameId, "Star Game")).Code, Is.EqualTo(LibraryEditResult.JobRunning));
        AssertUntouched();
    }

    [Test]
    public async Task PointerAlsoClaimedByAnotherGame_ChangesNothing()
    {
        var gameId = await TwoReleaseGameAsync();
        var (otherId, _) = await QueueAsync("mystery.xci");
        var request = ManualMystery with { Title = "Space Odyssey" };
        Assert.That((await Resolver().ResolveInJobAsync(otherId, request, await AdminAsync(), default)).Code, Is.Null);
        await using (var db = Service<IDbConnectionFactory>().Create())
            await db.ExecuteAsync("UPDATE roms SET external_file_name = @name WHERE game_id <> @gameId",
                new { name = Path.GetFileName(Sidecar), gameId = gameId.ToString() });

        var result = await RetitleAsync(gameId, "Star Game");

        Assert.That(result.Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        AssertUntouched();
    }

    [TestCase(false, HttpStatusCode.Forbidden, TestName = "TitleEdit_OverHttp_NeedsTheRomManagerRole plain user")]
    [TestCase(true, HttpStatusCode.OK, TestName = "TitleEdit_OverHttp_NeedsTheRomManagerRole rom manager")]
    public async Task TitleEdit_OverHttp_NeedsTheRomManagerRole(bool romManager, HttpStatusCode expected)
    {
        var gameId = await TwoReleaseGameAsync();
        using var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "someone", romManager ? Roles.Of(Roles.RomManager) : Roles.Of());
        (await client.LoginAsync("someone", TestUsers.Password)).Dispose();

        using var response = await client.PatchAsync($"/api/games/{gameId}", new { title = "Star Game" });

        Assert.That(response.StatusCode, Is.EqualTo(expected));
        Assert.That(File.Exists(StarUsa), Is.EqualTo(romManager));
    }
}
