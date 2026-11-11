using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec T9 and I4–I5 for Review: an external item is re-hashed streamed and moved, never read into memory.</summary>
public partial class IngestExternalPlacementTests
{
    private static readonly byte[] Mystery = RandomNumberGenerator.GetBytes(3000);
    private static readonly ReviewResolutionRequest ManualMystery = new() { Action = "Manual", Title = "Mystery Game", Regions = [], Languages = [], Flags = [] };

    private string MysteryZip => Path.Combine(_factory.Root, "roms", "ext", "M", "Mystery Game.zip");

    private string MysterySidecar(string extension) => Path.Combine(_factory.Root, "roms", "ext", "M", $"Mystery Game.Mystery Game{extension}");

    private IIngestReviewRepository Reviews => Service<IIngestReviewRepository>();

    private async Task<long> ItemIdAsync() =>
        (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(Mystery)), default))!.Id;

    /// <summary>Ingests an unmatched loose file, which waits in Review.</summary>
    private async Task<(long Id, IngestFile File)> QueueAsync(string name)
    {
        var file = Drop(name, Mystery);
        Assert.That(Code(await IngestAsync(Pipeline(), file)), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        return (await ItemIdAsync(), file);
    }

    /// <summary>A new Admin each call, so a test can act twice.</summary>
    private async Task<AuditActor> AdminAsync()
    {
        var name = "boss" + Guid.NewGuid().ToString("N")[..8];
        var user = await TestUsers.CreateAsync(_factory, name, Roles.Of(Roles.Admin));
        return new AuditActor(user.Id, name, null);
    }

    private ReviewResolver Resolver(ExternalIngestHooks? hooks = null) =>
        ActivatorUtilities.CreateInstance<ReviewResolver>(_factory.Services, Pipeline(hooks));

    private async Task<string?> StatusAsync(long id) => (await Reviews.GetAsync(id, default))?.Status;

    [Test]
    public async Task Manual_PlacesTheOriginalFile_WithTheReviewAudit()
    {
        var (id, file) = await QueueAsync("mystery.XCI");

        var result = await Resolver().ResolveAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(File.ReadAllBytes(MysterySidecar(".xci")), Is.EqualTo(Mystery));
        using (var zip = LibraryZip.Open(MysteryZip, Service<InfoJsonReader>()))
            Assert.That(zip.InfoJson!.Game!.Roms.Single().FileName, Is.EqualTo("Mystery Game.xci"));
        Assert.That(await StatusAsync(id), Is.EqualTo("Resolved"));
        Assert.That((await AuditAsync()).Last(), Is.EqualTo((AuditActions.Ingested, (string?)"review resolve (Manual)")));
    }

    [Test]
    public async Task LibretroDat_OfAnNspEntry_ForARawXci_KeepsXciAndTheUnverifiedFlag()
    {
        var (id, _) = await QueueAsync("dump.xci");
        var request = new ReviewResolutionRequest { Action = "Dat", DatSystem = ExternalTestHandler.SystemId, DatGameName = GameName };

        var result = await Resolver().ResolveAsync(id, request, await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        using var zip = LibraryZip.Open(ZipPath, Service<InfoJsonReader>());
        var rom = zip.InfoJson!.Game!.Roms.Single();
        Assert.That(rom.FileName, Is.EqualTo($"{GameName} (Unverified).xci"));
        Assert.That(rom.Flags, Does.Contain("Unverified"));
        Assert.That(File.ReadAllBytes(Path.Combine(Bucket, rom.ExternalFileName!)), Is.EqualTo(Mystery));
    }

    /// <summary>Queues an archived item: the job extracts the archive, and its unmatched entry waits in Review.</summary>
    private async Task<(long Id, string Archive)> QueueArchiveAsync()
    {
        var archive = Archive("pack.zip", Mystery);
        await Job(Pipeline()).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);
        Assert.That(_items.Single(i => i.ArchiveEntry == "dump.nsp").Outcome, Is.EqualTo(IngestOutcome.Review));
        return (await ItemIdAsync(), archive);
    }

    [Test]
    public async Task ArchivedItem_IsPlacedFromItsReExtractedCopy()
    {
        var (id, archive) = await QueueArchiveAsync();

        var result = await Resolver().ResolveAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        Assert.That(File.ReadAllBytes(MysterySidecar(".nsp")), Is.EqualTo(Mystery));
        Assert.That(File.Exists(archive), Is.True, "the archive's fate is the next ingest run's");
        Assert.That(await StatusAsync(id), Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task ArchivedItem_FailedPlacementWithAChangedContainer_KeepsPendingAndTheVerifiedCopy()
    {
        var (id, archive) = await QueueArchiveAsync();
        var hooks = new ExternalIngestHooks
        {
            Writer = FailVerify(() =>
            {
                File.Delete(archive);
                Archive("pack.zip", RandomNumberGenerator.GetBytes(16));
            }, MysteryZip),
        };

        var result = await Resolver(hooks).ResolveAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(result.Code, Is.EqualTo(IngestErrors.VerifyFailed));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(MysterySidecar(".nsp")), Is.EqualTo(Mystery), "the only verified copy is kept");
    }

    [TestCase("sha1", TestName = "ChangedSource_IsVerifyFailed_AndKeepsPending other bytes")]
    [TestCase("during", TestName = "ChangedSource_IsVerifyFailed_AndKeepsPending changed while hashing")]
    [TestCase("after", TestName = "ChangedSource_IsVerifyFailed_AndKeepsPending changed after hashing")]
    [TestCase("cap", TestName = "ChangedSource_IsVerifyFailed_AndKeepsPending grew past the size cap")]
    [TestCase("staged", TestName = "ChangedSource_IsVerifyFailed_AndKeepsPending staged copy with another sha1")]
    public async Task ChangedSource_IsVerifyFailed_AndKeepsPending(string change)
    {
        var (id, file) = await QueueAsync("mystery.xci");
        static void Append(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(length);
        }
        var hooks = change switch
        {
            "during" => new ExternalIngestHooks { BeforeHash = f => Append(f.FullPath, Mystery.Length + 4) },
            "after" => new ExternalIngestHooks { BeforePlacement = f => Append(f.FullPath, Mystery.Length + 4) },
            "cap" => new ExternalIngestHooks { BeforeHash = f => Append(f.FullPath, new ExternalTestHandler().MaxRomSizeBytes + 1) },
            "staged" => new ExternalIngestHooks
            {
                Move = new ExternalMoveHooks
                {
                    Rename = (from, to) => from.EndsWith("mystery.xci", StringComparison.Ordinal)
                        ? new NoReplaceRenameResult(NoReplaceRenameStatus.CrossDevice) : NoReplaceRename.Move(from, to),
                    WrapSource = source =>
                    {
                        var bytes = new byte[Mystery.Length];
                        source.ReadExactly(bytes);
                        bytes[0] ^= 0xFF;
                        return new MemoryStream(bytes);
                    },
                },
            },
            _ => null,
        };
        if (change == "sha1")
        {
            var other = (byte[])Mystery.Clone();
            other[0] ^= 0xFF;
            File.WriteAllBytes(file.FullPath, other);
        }

        var result = await Resolver(hooks).ResolveAsync(id, ManualMystery, await AdminAsync(), default);

        Assert.That(result.Code, Is.EqualTo(IngestErrors.VerifyFailed));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(File.Exists(file.FullPath), Is.True, "the source stays in ingest");
        Assert.That(File.Exists(MysteryZip), Is.False);
    }

    [TestCase("hash", TestName = "AbortedRequest_StillCommitsAndResolves during hashing")]
    [TestCase("zip", TestName = "AbortedRequest_StillCommitsAndResolves during the ZIP write")]
    public async Task AbortedRequest_StillCommitsAndResolves(string when)
    {
        var (id, _) = await QueueAsync("mystery.xci");
        using var aborted = new CancellationTokenSource();
        var hooks = when == "hash"
            ? new ExternalIngestHooks { BeforeHash = _ => aborted.Cancel() }
            : new ExternalIngestHooks { Writer = new LibraryZipWriterHooks { AfterTempClosed = aborted.Cancel } };

        var result = await Resolver(hooks).ResolveAsync(id, ManualMystery, await AdminAsync(), aborted.Token);

        Assert.That(aborted.IsCancellationRequested, Is.True);
        Assert.That(result.Code, Is.Null);
        Assert.That(File.ReadAllBytes(MysterySidecar(".xci")), Is.EqualTo(Mystery));
        Assert.That(await StatusAsync(id), Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task RomManagerWithoutAdmin_CannotResolve()
    {
        var (id, file) = await QueueAsync("mystery.xci");
        using var manager = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "manager-only", Roles.Of(Roles.RomManager));
        (await manager.LoginAsync("manager-only", TestUsers.Password)).Dispose();

        using var response = await manager.PostAsync($"/api/review/{id}/resolve", ManualMystery);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await StatusAsync(id), Is.EqualTo("Pending"));
        Assert.That(File.Exists(file.FullPath), Is.True);
    }
}
