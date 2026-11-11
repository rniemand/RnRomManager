using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class IngestFileScannerTests
{
    private TempDir _dir = null!;
    private IngestFileScanner _scanner = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var handlers = new[] { "gb", "nes" }.Select(id =>
        {
            var h = Substitute.For<ISystemHandler>();
            h.EmbedRomsInZip.Returns(true);
            h.Id.Returns(id);
            return h;
        });
        _scanner = new IngestFileScanner(Options.Create(new PathsOptions { Ingest = _dir.Combine("ingest") }),
            new SystemRegistry(handlers.Select((h, i) => { h.Family.Returns("f" + i); return h; }).Append(new ExternalTestHandler())),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestFileScanner>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private void Write(string relative, string text = "x")
    {
        var path = _dir.Combine("ingest", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Test]
    public void ScanFailed_ListsFilesWithReasons_PerSystemInRegistryOrder()
    {
        Write("gb/_failed/b.gb", "1234");
        Write("gb/_failed/b.gb.error.txt", "reason: InvalidHeader \ndetails: bad logo\njob: x\n");
        Write("gb/_failed/a.gb");
        Write("gb/_failed/a.gb.error.txt", "details: no reason line\n");
        Write("gb/_failed/c.gb");
        Write("gb/_failed/orphan.gb.ERROR.TXT", "reason: X\n");
        Write("gb/_failed/.hidden");
        Write("gb/_failed/Thumbs.db");
        Write("gb/_failed/sub/deep.gb");
        Write("gb/pending.gb");

        var result = _scanner.ScanFailed();

        Assert.That(result.Select(s => s.SystemId), Is.EqualTo(new[] { "gb", "nes", "ext" }));
        Assert.That(result[0].Files.Select(f => (f.Name, f.Reason)), Is.EqualTo(new[]
        {
            ("a.gb", (string?)null), ("b.gb", "InvalidHeader"), ("c.gb", null),
        }));
        Assert.That(result[0].Files[1].SizeBytes, Is.EqualTo(4));
        Assert.That(result[1].Files, Is.Empty); // missing folder
    }

    // --- external systems (spec G4) ---

    private IReadOnlyList<FailedFile> ExtFailed() => _scanner.ScanFailed().Single(s => s.SystemId == "ext").Files;

    private IReadOnlyList<IngestFile> ExtPending() => _scanner.Scan().Single(s => s.SystemId == "ext").Files;

    [Test]
    public void External_ListsPendingAndFailedFiles_WithTheReason()
    {
        Write("ext/a.nsp", "1234");
        Write("ext/_failed/b.nsp", "12");
        Write("ext/_failed/b.nsp.error.txt", "reason: TooSmall\r\ndetails: x\n");
        Write("ext/_failed/sub/c.nsp");

        Assert.That(ExtPending().Select(f => (f.Name, f.SizeBytes)), Is.EqualTo(new[] { ("a.nsp", 4L) }));
        Assert.That(ExtFailed().Select(f => (f.Name, f.SizeBytes, f.Reason)), Is.EqualTo(new[] { ("b.nsp", 2L, (string?)"TooSmall") }));
    }

    [Test]
    public void External_OverlongOrMalformedFirstLine_IsNoReason_ButALargeBodyAfterAShortReasonParses()
    {
        Write("ext/_failed/long.nsp");
        Write("ext/_failed/long.nsp.error.txt", "reason: " + new string('x', 2000) + "\n");
        Write("ext/_failed/unterminated.nsp");
        Write("ext/_failed/unterminated.nsp.error.txt", "reason: TooSmall");
        Write("ext/_failed/second.nsp");
        Write("ext/_failed/second.nsp.error.txt", "details: first\nreason: TooSmall\n");
        Write("ext/_failed/big.nsp");
        Write("ext/_failed/big.nsp.error.txt", "reason: TooLarge\ndetails: " + new string('y', 200_000) + "\n");

        Assert.That(ExtFailed().Select(f => (f.Name, f.Reason)), Is.EqualTo(new[]
        {
            ("big.nsp", (string?)"TooLarge"), ("long.nsp", null), ("second.nsp", null), ("unterminated.nsp", null),
        }));
    }

    [Test]
    public void External_LinkedSystemFolder_AndLinkedFailedFolder_ListNothing()
    {
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "theirs.nsp"), "x");
        Directory.CreateDirectory(_dir.Combine("ingest", "ext"));
        var failed = _dir.Combine("ingest", "ext", "_failed");
        Assert.That(TestLinks.TryLinkDirectory(failed, outside), Is.True, "this account can't create a directory link");
        try
        {
            Assert.That(ExtFailed(), Is.Empty);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(failed);
        }

        Directory.Delete(_dir.Combine("ingest", "ext"));
        TestLinks.TryLinkDirectory(_dir.Combine("ingest", "ext"), outside);
        try
        {
            Assert.That(ExtPending(), Is.Empty);
            Assert.That(ExtFailed(), Is.Empty);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(_dir.Combine("ingest", "ext"));
        }
    }

    [Test]
    public void External_LinkedOrFifoDiagnostic_IsNeverOpened()
    {
        Write("ext/_failed/a.nsp");
        var diagnostic = _dir.Combine("ingest", "ext", "_failed", "a.nsp.error.txt");
        var target = _dir.Combine("outside.txt");
        File.WriteAllText(target, "reason: FromOutside\n");
        // A FIFO would block a plain open forever; a link would read a file outside the folder.
        if (!TestLinks.TryCreateFifo(diagnostic) && !TestLinks.TryLinkFile(diagnostic, target))
            Assert.Ignore("this OS or account can create neither a FIFO nor a file link");

        Assert.That(ExtFailed().Select(f => (f.Name, f.Reason)), Is.EqualTo(new[] { ("a.nsp", (string?)null) }));
    }
}
