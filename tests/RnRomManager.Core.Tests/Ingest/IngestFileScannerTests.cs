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
            h.Id.Returns(id);
            return h;
        });
        _scanner = new IngestFileScanner(Options.Create(new PathsOptions { Ingest = _dir.Combine("ingest") }), new SystemRegistry(handlers));
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

        Assert.That(result.Select(s => s.SystemId), Is.EqualTo(new[] { "gb", "nes" }));
        Assert.That(result[0].Files.Select(f => (f.Name, f.Reason)), Is.EqualTo(new[]
        {
            ("a.gb", (string?)null), ("b.gb", "InvalidHeader"), ("c.gb", null),
        }));
        Assert.That(result[0].Files[1].SizeBytes, Is.EqualTo(4));
        Assert.That(result[1].Files, Is.Empty); // missing folder
    }
}
