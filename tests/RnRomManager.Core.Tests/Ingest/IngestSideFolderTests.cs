using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec W6: an external system's moves to <c>_failed/</c> and <c>_duplicates/</c>.</summary>
public class IngestSideFolderTests
{
    private static readonly AuditActor Actor = new(null, null, Guid.NewGuid());

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private IngestPipeline _pipeline = null!;
    private string _ingest = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Roms = _dir.Combine("roms"), Ingest = _dir.Combine("ingest"), Cache = _dir.Combine("cache") };
        var systems = new SystemRegistry([new ExternalTestHandler()]);
        var options = Options.Create(_paths);
        _pipeline = new IngestPipeline(new DatCache(options, systems, NullLogger<DatCache>.Instance), systems, Substitute.For<IRomRepository>(),
            Substitute.For<IGameRepository>(), Substitute.For<ILibraryMirror>(), Substitute.For<IIntegrityIssueRepository>(),
            Substitute.For<IIngestReviewRepository>(), new InfoJsonReader([], systems),
            TestMedia.Offline(),
            options, Substitute.For<IAuditLog>(), new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance);
        _ingest = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalTestHandler.SystemId)).FullName;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private IngestFile Drop(string name)
    {
        var path = Path.Combine(_ingest, name);
        File.WriteAllText(path, "rom bytes");
        return new IngestFile(ExternalTestHandler.SystemId, path, name, 9, File.GetLastWriteTimeUtc(path));
    }

    private string Side(string folder, string name = "") => Path.Combine(_ingest, folder, name);

    [Test]
    public void MovesAside_BeforeAnyHash_WithItsDiagnostic()
    {
        var file = Drop("game.nsp");

        Assert.That(_pipeline.MoveToFailed(file, Actor, "TooSmall", "9 bytes"), Is.Null);
        Assert.That(_pipeline.MoveToDuplicates(Drop("copy.nsp"), Actor), Is.Null);

        Assert.That(File.ReadAllText(Side("_failed", "game.nsp")), Is.EqualTo("rom bytes"));
        Assert.That(File.ReadAllText(Side("_failed", "game.nsp.error.txt")), Does.StartWith("reason: TooSmall\n"));
        Assert.That(File.Exists(Side("_duplicates", "copy.nsp")), Is.True);
        Assert.That(Directory.GetFiles(_ingest), Is.Empty);
    }

    [Test]
    public void VanishedSource_IsAnIoError()
    {
        var file = Drop("game.nsp");
        File.Delete(file.FullPath);

        var result = _pipeline.MoveToDuplicates(file, Actor);

        Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
    }

    [Test]
    public void LinkedSideFolder_RejectsTheMove_AndKeepsTheSource()
    {
        var file = Drop("game.nsp");
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        Assert.That(TestLinks.TryLinkDirectory(Side("_failed"), outside), Is.True, "this account can't create a directory link");
        try
        {
            var result = _pipeline.MoveToFailed(file, Actor, "TooSmall", "9 bytes");

            Assert.That((result?.Outcome, result?.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
            Assert.That(File.Exists(file.FullPath), Is.True);
            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(Side("_failed"));
        }
    }

    [TestCase("", TestName = "LongName_WithAnOccupiedName_GetsABudgetedSuffix final name")]
    [TestCase(".tmp", TestName = "LongName_WithAnOccupiedName_GetsABudgetedSuffix staging name")]
    [TestCase(".error.txt", TestName = "LongName_WithAnOccupiedName_GetsABudgetedSuffix diagnostic name")]
    public void LongName_WithAnOccupiedName_GetsABudgetedSuffix(string occupiedSuffix)
    {
        var name = new string('n', 251) + ".nsp"; // 255 bytes
        IngestFile file;
        try
        {
            file = Drop(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore($"this filesystem can't hold a 255-byte name here: {ex.Message}");
            return;
        }
        // 245 bytes: the longest name whose ".error.txt" still fits a component.
        var first = new string('n', 241) + ".nsp";
        Directory.CreateDirectory(Side("_failed"));
        var occupant = Side("_failed", (first + occupiedSuffix).ToUpperInvariant());
        File.WriteAllText(occupant, "someone else's");

        Assert.That(_pipeline.MoveToFailed(file, Actor, "TooSmall", "9 bytes"), Is.Null);

        var moved = new string('n', 237) + " (2).nsp";
        Assert.That(Encoding.UTF8.GetByteCount(moved + ".error.txt"), Is.EqualTo(255));
        Assert.That(File.ReadAllText(Side("_failed", moved)), Is.EqualTo("rom bytes"));
        Assert.That(File.Exists(Side("_failed", moved + ".error.txt")), Is.True);
        Assert.That(File.ReadAllText(occupant), Is.EqualTo("someone else's"));
    }

    [Test]
    public void SidePath_ReservesTheDiagnosticOnlyWhenAsked_AndIsNullWhenNothingFits()
    {
        var folder = Side("_duplicates");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.nsp.error.txt"), "");

        Assert.That(ExternalRomName.AllocateSidePath(folder, "a.nsp"), Is.EqualTo(Path.Combine(folder, "a.nsp")));
        Assert.That(ExternalRomName.AllocateSidePath(folder, "a.nsp", ".error.txt"), Is.EqualTo(Path.Combine(folder, "a (2).nsp")));
        Assert.That(ExternalRomName.AllocateSidePath(folder, "a." + new string('x', 260)), Is.Null, "an extension alone over the budget");
    }
}
