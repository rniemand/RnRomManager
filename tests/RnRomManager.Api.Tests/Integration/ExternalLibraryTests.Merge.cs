using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec W4: merging an external game moves every file of the source beside the target ZIP.</summary>
public partial class ExternalLibraryTests
{
    private string MovedEurope => Path.Combine(MysteryFolder, $"Mystery Game.{EuropeName}.nsp");

    /// <summary>Runs <c>before</c>, the mirror write, then <c>after</c>; either may throw.</summary>
    private sealed class HookedMirror(ILibraryMirror inner, Action? before = null, Action? after = null) : ILibraryMirror
    {
        public async Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken)
        {
            before?.Invoke();
            await inner.ApplyAsync(replace, delete, cancellationToken);
            after?.Invoke();
        }
    }

    /// <summary>Puts a copy in place of <paramref name="path"/>: same bytes, but not the file that was published.</summary>
    private static void Replace(string path)
    {
        File.Copy(path, path + ".copy");
        File.Delete(path);
        File.Move(path + ".copy", path);
    }

    private Task<LibraryEditResult> MergeAsync(Guid from, Guid into, LibraryEditor? editor = null) =>
        (editor ?? EditorWith()).MergeAsync(from, into, Editor, default);

    [Test]
    public async Task Merge_RenamesEveryFileBesideTheTarget_AndDeletesTheSourceZip()
    {
        var (space, mystery) = await TwoGamesAsync();

        var result = await MergeAsync(space, mystery);

        Assert.That((result.Code, result.GameId, result.TargetPublicationLost), Is.EqualTo(((string?)null, (Guid?)mystery, false)));
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom));
        Assert.That(File.ReadAllBytes(MovedEurope), Is.EqualTo(Europe));
        Assert.That(File.Exists(Sidecar) || File.Exists(EuropeSidecar) || File.Exists(ZipPath), Is.False);
        Assert.That(Read(MysteryZip).Roms.Select(r => r.ExternalFileName),
            Is.SupersetOf(new[] { Path.GetFileName(MovedUsa), Path.GetFileName(MovedEurope) }));
        Assert.That(Listed(MysteryZip), Has.None.StartsWith(LibraryZip.RomsFolder), "no ROM entry was passed to the writer");
    }

    [Test]
    public async Task ClashingName_TheRenamedReleaseNamesItsFileToo()
    {
        var (alpha, beta) = (RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var from = await CraftedGameAsync("Alpha", "Same.nsp", "Alpha.Same.nsp", alpha);
        var into = await CraftedGameAsync("Beta", "Same.nsp", "Beta.Same.nsp", beta);

        var result = await MergeAsync(from, into);

        Assert.That(result.Code, Is.Null);
        var target = Path.Combine(_factory.Root, "roms", "ext", "B", "Beta.zip");
        var merged = Read(target).Roms.Single(r => r.Metadata.Sha1 == Sha(alpha));
        Assert.That((merged.FileName, merged.ExternalFileName), Is.EqualTo(("Same (2).nsp", "Beta.Same (2).nsp")));
        Assert.That(File.ReadAllBytes(Path.Combine(_factory.Root, "roms", "ext", "B", "Beta.Same (2).nsp")), Is.EqualTo(alpha));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "roms", "ext", "A", "Alpha.zip")), Is.False);
    }

    [Test]
    public async Task MaxLengthClash_RenamesTheNoteWithTheRelease()
    {
        var name = new string('a', 251) + ".nsp"; // 255 characters, the logical name limit
        var (alpha, beta) = (RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var from = await CraftedGameAsync("Alpha", name, "Alpha.nsp", alpha, note: "alpha's note");
        var into = await CraftedGameAsync("Beta", name, "Beta.nsp", beta);

        var result = await MergeAsync(from, into);

        Assert.That(result.Code, Is.Null);
        var expected = new string('a', 247) + " (2).nsp";
        var target = Path.Combine(_factory.Root, "roms", "ext", "B", "Beta.zip");
        var merged = Read(target).Roms.Single(r => r.Metadata.Sha1 == Sha(alpha));
        Assert.That(merged.FileName, Is.EqualTo(expected));
        Assert.That(File.ReadAllBytes(Path.Combine(_factory.Root, "roms", "ext", "B", merged.ExternalFileName!)), Is.EqualTo(alpha));
        using var zip = ZipFile.OpenRead(target);
        using var note = new StreamReader(zip.GetEntry(LibraryZip.NoteEntry(expected))!.Open());
        Assert.That(note.ReadToEnd(), Is.EqualTo("alpha's note"));
    }

    [Test]
    public async Task MissingFile_OnlyItsPointerIsRenamed()
    {
        var (space, mystery) = await TwoGamesAsync();
        File.Delete(Sidecar);

        var result = await MergeAsync(space, mystery);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(MovedUsa), Is.False);
        Assert.That(File.ReadAllBytes(MovedEurope), Is.EqualTo(Europe));
        Assert.That(Read(MysteryZip).Roms.Select(r => r.ExternalFileName), Does.Contain(Path.GetFileName(MovedUsa)));
    }

    [Test]
    public async Task FailedTargetWrite_MovesEveryFileBack()
    {
        var (space, mystery) = await TwoGamesAsync();
        var (spaceZip, mysteryZip) = (File.ReadAllBytes(ZipPath), File.ReadAllBytes(MysteryZip));

        var result = await MergeAsync(space, mystery, EditorWith(new ExternalEditHooks { Writer = FailVerify(zipPath: MysteryZip) }));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.VerifyFailed));
        AssertNotMoved(spaceZip, mysteryZip);
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
        Assert.That(File.Exists(MovedEurope), Is.False);
    }

    [Test]
    public async Task LaterMoveFailure_MovesEarlierFilesBack_BeforeAnyWrite()
    {
        var (space, mystery) = await TwoGamesAsync();
        var (spaceZip, mysteryZip) = (File.ReadAllBytes(ZipPath), File.ReadAllBytes(MysteryZip));
        var renames = 0;
        var hooks = new ExternalEditHooks
        {
            Move = new ExternalMoveHooks
            {
                Rename = (from, to) => ++renames == 2
                    ? new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, Details: "refused") : NoReplaceRename.Move(from, to),
            },
        };

        var result = await MergeAsync(space, mystery, EditorWith(hooks));

        Assert.That(result.Code, Is.EqualTo(LibraryEditResult.IoError));
        AssertNotMoved(spaceZip, mysteryZip);
        Assert.That(File.ReadAllBytes(EuropeSidecar), Is.EqualTo(Europe));
    }

    [Test]
    public async Task TargetReplacedAfterTheMirror_KeepsTheSourceZip()
    {
        var (space, mystery) = await TwoGamesAsync();
        var mirror = new HookedMirror(Service<ILibraryMirror>(), after: () => Replace(MysteryZip));

        var result = await MergeAsync(space, mystery, EditorWith(mirror: mirror));

        Assert.That(result.TargetPublicationLost, Is.True);
        Assert.That(File.Exists(ZipPath), Is.True, "the source ZIP is kept for recovery (W7)");
        Assert.That(File.ReadAllBytes(MovedUsa), Is.EqualTo(Rom), "the target replacement still lists the moved files");
    }

    [Test]
    public async Task BulkMerge_ListsASourceAfterAnOrdinaryDbFailure_ButNotAfterALostTarget()
    {
        var bytes = Enumerable.Range(0, 4).Select(_ => RandomNumberGenerator.GetBytes(32)).ToArray();
        var alpha = await CraftedGameAsync("Alpha", "Alpha.nsp", "Alpha.Alpha.nsp", bytes[0]);
        var beta = await CraftedGameAsync("Beta", "Beta.nsp", "Beta.Beta.nsp", bytes[1]);
        var gamma = await CraftedGameAsync("Gamma", "Gamma.nsp", "Gamma.Gamma.nsp", bytes[2]);
        var delta = await CraftedGameAsync("Delta", "Delta.nsp", "Delta.Delta.nsp", bytes[3]);
        using var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "merger", TestUsers.AllRoles);
        (await client.LoginAsync("merger", TestUsers.Password)).Dispose();

        async Task<Guid[]> MergedAsync(Guid source, Guid target, Action hook)
        {
            _mirrorHook = hook;
            using var response = await client.PostAsync("/api/reports/duplicates/merge", new { targetGameId = target, sourceGameIds = new[] { source } });
            _mirrorHook = null;
            var problem = await AuthClient.JsonAsync(response);
            Assert.That((response.StatusCode, problem.GetProperty("code").GetString()),
                Is.EqualTo((HttpStatusCode.InternalServerError, LibraryEditResult.DbWriteFailed)));
            return [.. problem.GetProperty("merged").EnumerateArray().Select(m => m.GetGuid())];
        }

        Assert.That(await MergedAsync(alpha, beta, () => throw new InvalidOperationException("db down")), Is.EqualTo(new[] { alpha }));
        var gammaZip = Path.Combine(_factory.Root, "roms", "ext", "G", "Gamma.zip");
        var deltaZip = Path.Combine(_factory.Root, "roms", "ext", "D", "Delta.zip");
        Assert.That(await MergedAsync(gamma, delta, () =>
        {
            Replace(deltaZip);
            throw new InvalidOperationException("db down");
        }), Is.Empty);
        Assert.That(File.Exists(gammaZip), Is.True);
    }
}
