using System.Reflection;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec T9: a retry of an archived Review item whose release is already in the library.</summary>
public partial class ExternalLibraryTests
{
    /// <summary>A review repository whose first call of one method fails, as a DB outage at that moment would.</summary>
    public class FailFirstResolution : DispatchProxy
    {
        public IIngestReviewRepository Inner { get; set; } = null!;
        public string Method { get; set; } = nameof(IIngestReviewRepository.SetResolutionAsync);
        private bool _failed;

        public static IIngestReviewRepository Wrap(IIngestReviewRepository inner, string method = nameof(IIngestReviewRepository.SetResolutionAsync))
        {
            var proxy = Create<IIngestReviewRepository, FailFirstResolution>();
            ((FailFirstResolution)(object)proxy).Inner = inner;
            ((FailFirstResolution)(object)proxy).Method = method;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == Method && !_failed)
            {
                _failed = true;
                return Task.FromException(new InvalidOperationException("db down"));
            }
            return method.Invoke(Inner, args);
        }
    }

    private ReviewResolver ResolverWith(IngestPipeline pipeline, IIngestReviewRepository? reviews = null) => reviews is null
        ? ActivatorUtilities.CreateInstance<ReviewResolver>(_factory.Services, pipeline)
        : ActivatorUtilities.CreateInstance<ReviewResolver>(_factory.Services, pipeline, reviews);

    /// <summary>An archived item whose release is in the library while its row is still Pending.</summary>
    /// <param name="failure"><c>mirror</c>: the DB has no rows for it; <c>callback</c>: only the Resolved update failed.</param>
    private async Task<long> CommittedButPendingAsync(string failure)
    {
        var (id, _) = await QueueArchiveAsync();
        ReviewResolver resolver;
        if (failure == "mirror")
        {
            var mirror = Substitute.For<ILibraryMirror>();
            mirror.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));
            resolver = ResolverWith(Pipeline(mirror: mirror));
        }
        else
        {
            resolver = ResolverWith(Pipeline(), FailFirstResolution.Wrap(Reviews));
        }

        var first = await resolver.ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(first.Code, Is.EqualTo(IngestErrors.DbWriteFailed));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(MysterySidecar(".nsp")), Is.EqualTo(Mystery), "the release is committed");
        return id;
    }

    /// <summary>
    /// Points two mirrored games at each other's ZIP, stamps included, so the next sync takes both as unchanged and
    /// leaves the mirror stale: each game's row then names a ZIP of the other game.
    /// </summary>
    private async Task SwapZipPathsAsync(string first, string second)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        var rows = (await db.QueryAsync<(Guid Id, string Path, long Size, DateTime Time)>(
            "SELECT id, zip_path, zip_size_bytes, zip_mtime_utc FROM games WHERE zip_path IN @paths", new { paths = new[] { first, second } })).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        await db.ExecuteAsync("UPDATE games SET zip_path = CONCAT('swap/', id) WHERE id = @Id", new { rows[0].Id });
        await db.ExecuteAsync("UPDATE games SET zip_path = @Path, zip_size_bytes = @Size, zip_mtime_utc = @Time WHERE id = @Id",
            new { rows[1].Id, rows[0].Path, rows[0].Size, rows[0].Time });
        await db.ExecuteAsync("UPDATE games SET zip_path = @Path, zip_size_bytes = @Size, zip_mtime_utc = @Time WHERE id = @Id",
            new { rows[0].Id, rows[1].Path, rows[1].Size, rows[1].Time });
    }

    /// <summary>
    /// Resolves a loose item whose ZIP write fails after a replacement took its ingest name: the verified bytes go
    /// back as <c>mystery (2).xci</c> (I4).
    /// </summary>
    private async Task<(long Id, string Original, string Restored, ReviewResolutionResult Result)> RestoredToASuffixAsync(IIngestReviewRepository? reviews = null)
    {
        var (id, file) = await QueueAsync("mystery.xci");
        var hooks = new ExternalIngestHooks { Writer = FailVerify(() => File.WriteAllText(file.FullPath, "replacement"), MysteryZip) };
        var result = await ResolverWith(Pipeline(hooks, reviews: reviews)).ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default);
        var restored = Path.Combine(Ingest, "mystery (2).xci");
        Assert.That(File.ReadAllText(file.FullPath), Is.EqualTo("replacement"), "the replacement is preserved");
        Assert.That(File.ReadAllBytes(restored), Is.EqualTo(Mystery), "the verified bytes are back under a suffix");
        return (id, file.FullPath, restored, result);
    }

    [Test]
    public async Task RestoredToASuffix_ThePendingRowFollows_AndResolvesTheRecoveredBytes()
    {
        var (id, _, restored, result) = await RestoredToASuffixAsync();

        Assert.That(result.Code, Is.EqualTo(IngestErrors.VerifyFailed));
        var item = (await Reviews.GetAsync(id, default))!;
        Assert.That((item.Status, item.SourcePath, item.OriginalFileName), Is.EqualTo(("Pending", restored, "mystery.xci")));

        var retry = await Resolver().ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(retry.Code, Is.Null);
        Assert.That(File.ReadAllBytes(MysterySidecar(".xci")), Is.EqualTo(Mystery));
        Assert.That(File.Exists(restored), Is.False);
    }

    [Test]
    public async Task RestoredToASuffix_AutomaticIngestDoesNotCallItAPendingDuplicate()
    {
        var (_, _, restored, _) = await RestoredToASuffixAsync();
        var info = new FileInfo(restored);

        var result = await IngestAsync(Pipeline(), new IngestFile(ExternalTestHandler.SystemId, restored, info.Name, info.Length, info.LastWriteTimeUtc));

        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        Assert.That(File.Exists(restored), Is.True, "not moved to _duplicates/");
    }

    [Test]
    public async Task RestoredToASuffix_RetargetFailure_ReportsBothPaths_AndLeavesBothFiles()
    {
        var (id, original, restored, result) = await RestoredToASuffixAsync(
            FailFirstResolution.Wrap(Reviews, nameof(IIngestReviewRepository.RetargetPendingAsync)));

        Assert.That(result.Code, Is.EqualTo(IngestErrors.DbWriteFailed));
        Assert.That((await Reviews.GetAsync(id, default))!.SourcePath, Is.EqualTo(original), "the row still names the original path");
    }

    private async Task<HashSet<string>> RowIdsAsync()
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. await db.QueryAsync<string>("SELECT CONCAT('rom ', id) FROM roms UNION ALL SELECT CONCAT('image ', id) FROM images")];
    }

    private async Task<int> ReviewIngestedRowsAsync() =>
        (await AuditAsync()).Count(a => a.Action == AuditActions.Ingested && a.Details == "review resolve (Manual)");

    [TestCase("mirror", TestName = "Retry_ResolvesTheCommittedRelease_WithoutWriting after a mirror failure")]
    [TestCase("callback", TestName = "Retry_ResolvesTheCommittedRelease_WithoutWriting after a resolution update failure")]
    public async Task Retry_ResolvesTheCommittedRelease_WithoutWriting(string failure)
    {
        await FirstReleaseAsync(); // an unrelated game, whose rows must survive the retry's sync
        var id = await CommittedButPendingAsync(failure);
        var zip = File.ReadAllBytes(MysteryZip);
        var rows = await RowIdsAsync();
        Guid gameId;
        using (var library = LibraryZip.Open(MysteryZip, Service<InfoJsonReader>()))
            gameId = library.InfoJson!.Game!.GameId;

        var result = await Resolver().ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That((result.Code, result.GameId), Is.EqualTo(((string?)null, (Guid?)gameId)));
        Assert.That(await StatusAsync(id), Is.EqualTo("Resolved"));
        Assert.That(File.ReadAllBytes(MysteryZip), Is.EqualTo(zip), "no ZIP was written");
        Assert.That(await ReviewIngestedRowsAsync(), Is.EqualTo(1), "no second ingest audit row");
        Assert.That(rows, Is.SubsetOf(await RowIdsAsync()), "unrelated ROM and image rows keep their IDs");
    }

    [Test]
    public async Task AnotherArchiveWithTheSameSha1_IsDuplicate()
    {
        var (looseId, _) = await QueueAsync("mystery.xci");
        Assert.That((await Resolver().ResolveInJobAsync(looseId, ManualMystery, await AdminAsync(), default)).Code, Is.Null);
        var archive = Archive("other.zip", Mystery);
        var item = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(Mystery)), default))!;
        await Reviews.UpsertAsync(item with
        {
            Status = "Pending", SourcePath = archive, ArchivePath = archive, ArchiveEntry = "dump.nsp", OriginalFileName = "dump.nsp",
            GameId = null, ResolvedAt = null, ResolvedBy = null,
        }, default);
        var id = await ItemIdAsync();
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "admin-only", RnRomManager.Core.Users.Roles.Of(RnRomManager.Core.Users.Roles.Admin));
        (await admin.LoginAsync("admin-only", TestUsers.Password)).Dispose();

        using var response = await admin.PostAsync($"/api/review/{id}/resolve", ManualMystery);

        Assert.That(((int)response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((409, (string?)"Duplicate")));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(File.Exists(archive), Is.True);
    }

    [Test]
    public async Task EmbeddedOwner_IsDuplicate()
    {
        var (id, _) = await QueueArchiveAsync();
        // The same bytes in an embedded game's ZIP, which the retry's sync mirrors (ingest itself would call it a duplicate of the pending item).
        var embedded = TestLibrary.Game("Mystery", TestLibrary.Rom("Mystery.gb", Mystery));
        var zip = Path.Combine(_factory.Root, "roms", "gb", "M", "Mystery.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        await new LibraryZipWriter(Service<InfoJsonReader>()).WriteAsync(zip, null,
            new LibraryZipChanges(embedded, new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry("Mystery.gb")] = () => new MemoryStream(Mystery) }));

        var result = await Resolver().ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(result.Code, Is.EqualTo("Duplicate"));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
    }

    [TestCase("missing", TestName = "SameSourceOwner_ThatCantBeVerified_IsVerifyFailed missing file")]
    [TestCase("other bytes", TestName = "SameSourceOwner_ThatCantBeVerified_IsVerifyFailed equal-length file with another sha1")]
    [TestCase("unreadable", TestName = "SameSourceOwner_ThatCantBeVerified_IsVerifyFailed unreadable ZIP")]
    [TestCase("identity", TestName = "SameSourceOwner_ThatCantBeVerified_IsVerifyFailed ZIP of another game")]
    public async Task SameSourceOwner_ThatCantBeVerified_IsVerifyFailed(string damage)
    {
        if (damage == "identity")
            await FirstReleaseAsync();
        var id = await CommittedButPendingAsync("callback");
        switch (damage)
        {
            case "missing":
                File.Delete(MysterySidecar(".nsp"));
                break;
            case "other bytes":
                File.WriteAllBytes(MysterySidecar(".nsp"), RandomNumberGenerator.GetBytes(Mystery.Length));
                break;
            case "unreadable":
                File.WriteAllText(MysteryZip, "not a zip");
                break;
            default:
                await SwapZipPathsAsync(ZipRelative, "ext/M/Mystery Game.zip");
                break;
        }

        var result = await Resolver().ResolveInJobAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(result.Code, Is.EqualTo(IngestErrors.VerifyFailed));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(await ReviewIngestedRowsAsync(), Is.EqualTo(1));
    }
}
