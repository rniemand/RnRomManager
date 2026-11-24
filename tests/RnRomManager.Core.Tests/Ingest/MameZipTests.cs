using System.IO.Compression;
using RnRomManager.Core.Storage;
using System.Text;
using RnRomManager.Core.Ingest;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class MameZipTests
{
    private static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                if (content.Length == 0 && name.EndsWith('/'))
                    continue;
                using var stream = entry.Open();
                stream.Write(Encoding.ASCII.GetBytes(content));
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    private static string Crc(string content) => Crc32.ToHex(Encoding.ASCII.GetBytes(content));

    [Test]
    public void ReadEntries_ReturnsFileNamesAndCrcs_SkippingFolders()
    {
        using var zip = Zip(("sf2_30a.bin", "AAAA"), ("roms/", ""), ("roms/sf2_37a.bin", "B"));

        var (entries, error) = MameZip.ReadEntries(zip);

        Assert.That(error, Is.Null);
        Assert.That(entries, Is.EqualTo(new[] { new MameEntry("sf2_30a.bin", Crc("AAAA")), new MameEntry("sf2_37a.bin", Crc("B")) }));
        Assert.That(entries![0].Crc32, Does.Match("^[0-9a-f]{8}$"));
    }

    [Test]
    public void ReadEntries_NotAZip_IsAnError()
    {
        var (entries, error) = MameZip.ReadEntries(new MemoryStream(Encoding.ASCII.GetBytes("this is not a zip file at all")));

        Assert.That(entries, Is.Null);
        Assert.That(error, Does.StartWith("not a readable ZIP: "));
    }

    [Test]
    public void ReadEntries_MoreThan4096Files_IsAnError()
    {
        using var zip = Zip(Enumerable.Range(0, MameZip.MaxEntries + 1).Select(i => ($"f{i}", "")).ToArray());

        var (entries, error) = MameZip.ReadEntries(zip);

        Assert.That(entries, Is.Null);
        Assert.That(error, Is.EqualTo("more than 4096 files in the set ZIP"));
    }

    [Test]
    public void ReadSevenZipEntries_ReturnsFileNamesAndCrcs()
    {
        using var stream = File.OpenRead(TestFixtures.Basic7zPath);

        var (entries, error) = MameZip.ReadSevenZipEntries(stream);

        Assert.That(error, Is.Null);
        Assert.That(entries, Is.EqualTo(new[] { new MameEntry(TestFixtures.Basic7zEntry, Crc32.ToHex(TestFixtures.Basic7zRom())) }));
    }

    [Test]
    public void ReadSevenZipEntries_NotA7z_IsAnError()
    {
        var (entries, error) = MameZip.ReadSevenZipEntries(new MemoryStream(Encoding.ASCII.GetBytes("this is not a 7z file at all")));

        Assert.That(entries, Is.Null);
        Assert.That(error, Does.StartWith("not a readable 7z: "));
    }

    [Test]
    public void ReadSevenZipEntries_Truncated_IsAnError_NotAThrow()
    {
        var bytes = File.ReadAllBytes(TestFixtures.Basic7zPath);

        var (entries, error) = MameZip.ReadSevenZipEntries(new MemoryStream(bytes[..(bytes.Length / 2)]));

        Assert.That(entries, Is.Null);
        Assert.That(error, Does.StartWith("not a readable 7z: "));
    }
}
