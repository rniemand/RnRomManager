using Dapper;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec W5: deleting an external release or game moves its files to <c>ingest/ext/_removed/</c>.</summary>
public partial class ExternalLibraryTests
{
    private string Removed => Path.Combine(Ingest, "_removed");
    private string RemovedUsa => Path.Combine(Removed, $"{GameName}.xci");
    private string RemovedEurope => Path.Combine(Removed, $"{EuropeName}.nsp");

    private async Task<LibraryEditResult> DeleteReleaseAsync(Guid gameId, byte[] release, LibraryEditor? editor = null) =>
        await (editor ?? EditorWith()).DeleteReleaseAsync(gameId, await RomIdAsync(release), Editor, default);

    private Task<LibraryEditResult> DeleteGameAsync(Guid gameId, LibraryEditor? editor = null) =>
        (editor ?? EditorWith()).DeleteGameAsync(gameId, Editor, default);

    private async Task<List<(string Action, string? TargetPath)>> AuditTargetsAsync()
    {
        await Service<RnRomManager.Api.AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. await db.QueryAsync<(string, string?)>("SELECT action, target_path FROM audit_events ORDER BY id")];
    }

    [Test]
    public async Task DeleteRelease_MovesItsFileToRemoved_WithASuffixOnAClash_AndTheAuditNamesIt()
    {
        var gameId = await TwoReleaseGameAsync();
        Directory.CreateDirectory(Removed);
        File.WriteAllText(RemovedUsa, "an earlier removal");

        var result = await DeleteReleaseAsync(gameId, Rom);

        Assert.That(result.Code, Is.Null);
        var moved = Path.Combine(Removed, $"{GameName} (2).xci");
        Assert.That(File.ReadAllBytes(moved), Is.EqualTo(Rom));
        Assert.That(File.ReadAllText(RemovedUsa), Is.EqualTo("an earlier removal"));
        Assert.That(File.Exists(Sidecar), Is.False);
        Assert.That(Read(ZipPath).Roms.Select(r => r.ExternalFileName), Is.EqualTo(new[] { Path.GetFileName(EuropeSidecar) }));
        var deleted = (await AuditTargetsAsync()).Last(a => a.Action == AuditActions.ReleaseDeleted);
        Assert.That(deleted.TargetPath, Does.EndWith($"_removed/{GameName} (2).xci"));
    }

    [Test]
    public async Task DeleteGame_MovesEveryFileToRemoved_AndDeletesTheZip()
    {
        var gameId = await TwoReleaseGameAsync();

        var result = await DeleteGameAsync(gameId);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.ReadAllBytes(RemovedUsa), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(RemovedEurope), Is.EqualTo(Europe));
        Assert.That(File.Exists(ZipPath) || File.Exists(Sidecar) || File.Exists(EuropeSidecar), Is.False);
        Assert.That((await AuditTargetsAsync()).Select(a => a.Action), Does.Contain(AuditActions.GameDeleted));
    }

    [Test]
    public async Task DeletingTheLastRelease_DeletesTheGame()
    {
        var (_, mystery) = await TwoGamesAsync();

        var result = await DeleteReleaseAsync(mystery, Mystery);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(MysteryZip), Is.False);
        Assert.That(File.ReadAllBytes(Path.Combine(Removed, "Mystery Game.xci")), Is.EqualTo(Mystery));
        var actions = (await AuditTargetsAsync()).Select(a => a.Action).ToList();
        Assert.That(actions, Does.Contain(AuditActions.GameDeleted));
        Assert.That(actions, Does.Not.Contain(AuditActions.ReleaseDeleted));
    }

    [Test]
    public async Task FailedZipWrite_MovesTheFileBack()
    {
        var gameId = await TwoReleaseGameAsync();
        var zip = File.ReadAllBytes(ZipPath);

        var result = await DeleteReleaseAsync(gameId, Rom, EditorWith(new ExternalEditHooks { Writer = FailVerify() }));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(zip));
        Assert.That(Directory.Exists(Removed) ? Directory.GetFiles(Removed) : [], Is.Empty);
    }

    [Test]
    public async Task MissingFile_IsSkipped()
    {
        var gameId = await TwoReleaseGameAsync();
        File.Delete(Sidecar);

        var result = await DeleteReleaseAsync(gameId, Rom);

        Assert.That(result.Code, Is.Null);
        Assert.That(Directory.Exists(Removed) ? Directory.GetFiles(Removed) : [], Is.Empty);
        Assert.That(Read(ZipPath).Roms, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LinkedRemovedFolder_IsAnIoError_AndItsTargetIsUntouched()
    {
        var gameId = await TwoReleaseGameAsync();
        var zip = File.ReadAllBytes(ZipPath);
        var outside = Directory.CreateDirectory(Path.Combine(_factory.Root, "outside")).FullName;
        if (Directory.Exists(Removed))
            Directory.Delete(Removed);
        if (!TestLinks.TryLinkDirectory(Removed, outside))
            Assert.Ignore("this account can't create a directory link");
        try
        {
            var result = await DeleteReleaseAsync(gameId, Rom);

            Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
            Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
            Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(zip));
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(Removed);
        }
    }

    [Test]
    public async Task ReplacementZipAtTheSource_IsKept_AndTheFilesGoBack()
    {
        var gameId = await TwoReleaseGameAsync();
        byte[]? replacement = null;
        var hooks = new ExternalEditHooks
        {
            Move = new ExternalMoveHooks
            {
                AfterStaging = _ =>
                {
                    if (replacement is not null)
                        return;
                    File.WriteAllText(ZipPath + ".copy", "a replacement");
                    File.Delete(ZipPath);
                    File.Move(ZipPath + ".copy", ZipPath);
                    replacement = File.ReadAllBytes(ZipPath);
                },
            },
        };

        var result = await DeleteGameAsync(gameId, EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        Assert.That(File.ReadAllBytes(ZipPath), Is.EqualTo(replacement));
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
    }

    [Test]
    public async Task FailedZipDeletion_MovesTheFilesBack()
    {
        Assume.That(OperatingSystem.IsWindows(), "only Windows refuses to delete an open file");
        var gameId = await TwoReleaseGameAsync();
        using var held = new FileStream(ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await DeleteGameAsync(gameId);

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        Assert.That(File.Exists(ZipPath), Is.True);
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
    }

    [Test]
    public async Task OccupiedRemovedTemp_ForcesASuffixedName()
    {
        var gameId = await TwoReleaseGameAsync();
        Directory.CreateDirectory(Removed);
        File.WriteAllText(RemovedUsa + ".tmp", "someone's temp");

        var result = await DeleteReleaseAsync(gameId, Rom);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.ReadAllBytes(Path.Combine(Removed, $"{GameName} (2).xci")), Is.EqualTo(Rom));
        Assert.That(File.ReadAllText(RemovedUsa + ".tmp"), Is.EqualTo("someone's temp"));
        Assert.That(File.Exists(RemovedUsa), Is.False);
    }

    [Test]
    public async Task DbFailure_KeepsTheFileInRemoved()
    {
        var gameId = await TwoReleaseGameAsync();
        var mirror = new HookedMirror(Service<ILibraryMirror>(), before: () => throw new InvalidOperationException("db down"));

        var result = await DeleteReleaseAsync(gameId, Rom, EditorWith(mirror: mirror));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));
        Assert.That(File.ReadAllBytes(RemovedUsa), Is.EqualTo(Rom));
        Assert.That(File.Exists(Sidecar), Is.False);
    }
}
